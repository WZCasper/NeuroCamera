using System.Threading;
using System.Windows.Media.Imaging;
using NeuroCamera.Common;
using NeuroCamera.Models;
using OpenCvSharp;

namespace NeuroCamera.Engine;

/// <summary>
/// Owns the entire capture -&gt; calibrate -&gt; correct -&gt; preview -&gt; virtual-camera pipeline on a
/// single dedicated background thread. Nothing in this class ever touches the WPF
/// Dispatcher; it only raises plain .NET events, which callers marshal to the UI thread
/// themselves. This guarantees the OpenCV work never runs on, and never blocks, the UI thread.
/// </summary>
public sealed class VideoEngine : IDisposable
{
    private static readonly TimeSpan VirtualCamRetryInterval = TimeSpan.FromSeconds(2);

    private readonly ImageProcessor _imageProcessor;
    private readonly CalibrationEngine _calibrationEngine;
    private readonly VirtualCamWriter _virtualCamWriter;

    private CancellationTokenSource? _cts;
    private Thread? _workerThread;
    private volatile CalibrationParameters _currentParameters = CalibrationParameters.Neutral;
    private bool _disposed;

    public VideoEngine(string haarCascadePath)
    {
        _imageProcessor = new ImageProcessor(haarCascadePath);
        _calibrationEngine = new CalibrationEngine(_imageProcessor);
        _calibrationEngine.ProgressChanged += (_, e) => CalibrationProgressChanged?.Invoke(this, e);
        _calibrationEngine.Completed += (_, parameters) =>
        {
            _currentParameters = parameters;
            CalibrationCompleted?.Invoke(this, parameters);
        };

        _virtualCamWriter = new VirtualCamWriter();
    }

    /// <summary>True while the dedicated capture thread is alive.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>Current calibration parameters applied to every processed frame.</summary>
    public CalibrationParameters CurrentParameters => _currentParameters;

    /// <summary>Raised on the background thread with each processed frame, ready for display.</summary>
    public event EventHandler<BitmapSource>? FrameReady;

    /// <summary>Raised on the background thread as the 15-second auto-tune progresses.</summary>
    public event EventHandler<CalibrationProgressEventArgs>? CalibrationProgressChanged;

    /// <summary>Raised on the background thread once auto-tune finishes with new parameters.</summary>
    public event EventHandler<CalibrationParameters>? CalibrationCompleted;

    /// <summary>Raised on the background thread whenever the virtual camera connection state changes.</summary>
    public event EventHandler<string>? VirtualCameraStatusChanged;

    /// <summary>Raised on the background thread if capture/processing fails unrecoverably.</summary>
    public event EventHandler<Exception>? ErrorOccurred;

    /// <summary>Opens the given physical camera and starts the dedicated processing thread.</summary>
    public void Start(int deviceIndex)
    {
        if (IsRunning)
        {
            Stop();
        }

        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;

        _workerThread = new Thread(() => RunLoop(deviceIndex, token))
        {
            IsBackground = true,
            Name = "NeuroCamera.VideoThread",
            Priority = ThreadPriority.AboveNormal
        };

        IsRunning = true;
        _workerThread.Start();
    }

    /// <summary>Stops the processing thread and releases the camera and virtual camera handles.</summary>
    public void Stop()
    {
        if (!IsRunning)
        {
            return;
        }

        _calibrationEngine.Cancel();
        _cts?.Cancel();
        _workerThread?.Join(TimeSpan.FromSeconds(3));
        _cts?.Dispose();
        _cts = null;
        _workerThread = null;
        _virtualCamWriter.Disconnect();
        IsRunning = false;
    }

    /// <summary>Starts the automated 15-second calibration. The camera must already be running.</summary>
    public void RequestCalibration()
    {
        if (!IsRunning)
        {
            throw new InvalidOperationException("Камера не запущена — сначала включите поток.");
        }

        _calibrationEngine.Start();
    }

    public void CancelCalibration() => _calibrationEngine.Cancel();

    private void RunLoop(int deviceIndex, CancellationToken token)
    {
        VideoCapture? capture = null;

        try
        {
            capture = new VideoCapture(deviceIndex, VideoCaptureAPIs.DSHOW);
            if (!capture.IsOpened())
            {
                capture.Dispose();
                capture = new VideoCapture(deviceIndex);
            }

            if (!capture.IsOpened())
            {
                RaiseError(new InvalidOperationException($"Не удалось открыть камеру с индексом {deviceIndex}."));
                return;
            }

            capture.Set(VideoCaptureProperties.FrameWidth, 1280);
            capture.Set(VideoCaptureProperties.FrameHeight, 720);
            capture.Set(VideoCaptureProperties.Fps, 30);

            using Mat rawFrame = new();
            DateTime lastVirtualCamRetry = DateTime.MinValue;

            while (!token.IsCancellationRequested)
            {
                if (!capture.Read(rawFrame) || rawFrame.Empty())
                {
                    Thread.Sleep(10);
                    continue;
                }

                if (_calibrationEngine.IsRunning)
                {
                    _calibrationEngine.FeedFrame(rawFrame);
                }

                CalibrationParameters parameters = _currentParameters;

                using (Mat processedFrame = _imageProcessor.ProcessFrame(rawFrame, parameters))
                {
                    BitmapSource preview = MatImageConverter.ToBitmapSource(processedFrame);
                    FrameReady?.Invoke(this, preview);

                    PumpVirtualCamera(processedFrame, ref lastVirtualCamRetry);
                }
            }
        }
        catch (Exception ex)
        {
            RaiseError(ex);
        }
        finally
        {
            if (capture is not null)
            {
                capture.Release();
                capture.Dispose();
            }
        }
    }

    private void PumpVirtualCamera(Mat processedFrame, ref DateTime lastRetry)
    {
        if (!_virtualCamWriter.IsConnected)
        {
            if (DateTime.UtcNow - lastRetry < VirtualCamRetryInterval)
            {
                return;
            }

            lastRetry = DateTime.UtcNow;
            bool connected = _virtualCamWriter.TryConnect();
            VirtualCameraStatusChanged?.Invoke(this, connected
                ? "Виртуальная камера подключена."
                : "Виртуальная камера не найдена. Откройте \"Unity Video Capture\" в Zoom/Teams/OBS, чтобы подключить.");

            if (!connected)
            {
                return;
            }
        }

        byte[] rgbaBytes = MatImageConverter.ToRgbaBytes(processedFrame, out int width, out int height);
        if (!_virtualCamWriter.SendFrame(rgbaBytes, width, height))
        {
            VirtualCameraStatusChanged?.Invoke(this, "Соединение с виртуальной камерой потеряно, переподключаюсь...");
        }
    }

    private void RaiseError(Exception ex) => ErrorOccurred?.Invoke(this, ex);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _imageProcessor.Dispose();
        _virtualCamWriter.Dispose();
        _disposed = true;
    }
}
