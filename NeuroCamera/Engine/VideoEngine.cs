using System.Threading;
using System.Windows.Media.Imaging;
using NeuroCamera.Common;
using NeuroCamera.Models;
using OpenCvSharp;

namespace NeuroCamera.Engine;

/// <summary>
/// Owns the entire capture -&gt; calibrate -&gt; correct -&gt; preview pipeline. Nothing in this class
/// ever touches the WPF Dispatcher; it only raises plain .NET events, which callers marshal to
/// the UI thread themselves. This guarantees the OpenCV work never runs on, and never blocks,
/// the UI thread.
///
/// Camera sessions run through <see cref="SerialSessionRunner"/> rather than a single owned
/// thread: opening a device (especially at 4K) can take longer than any reasonable timeout, so
/// <see cref="Start"/> no longer waits for the previous session to finish before starting the
/// next one - it hands both to the runner, which guarantees they never hold the camera at the
/// same time without the caller ever blocking.
/// </summary>
public sealed class VideoEngine : IDisposable
{
    private readonly ImageProcessor _imageProcessor;
    private readonly CalibrationEngine _calibrationEngine;
    private readonly SerialSessionRunner _sessions = new();
    private readonly LatestValueMailbox<BitmapSource> _previewMailbox = new();

    private volatile CalibrationParameters _currentParameters = CalibrationParameters.Neutral;

    // A quick, always-available brightness nudge on top of whatever calibration produced (or
    // on top of nothing, if calibration hasn't run yet) - see SetManualBrightnessOffset.
    private double _manualBrightnessOffset;

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
        _sessions.SessionFaulted += (_, ex) =>
        {
            CrashLog.Write("VideoEngine capture session", ex);
            RaiseError(ex);
        };
    }

    /// <summary>True while a capture session is currently active (not stopped/superseded/finished).</summary>
    public bool IsRunning => _sessions.IsActive;

    /// <summary>Current calibration parameters applied to every processed frame.</summary>
    public CalibrationParameters CurrentParameters => _currentParameters;

    /// <summary>
    /// Raised on the UI thread's dispatcher timing (via <see cref="PumpPreview"/>) with the most
    /// recently processed frame. Because it goes through a latest-value-wins mailbox, a consumer
    /// that falls behind never queues up frames - it simply gets the newest one available.
    /// </summary>
    public event EventHandler<BitmapSource>? FrameReady;

    /// <summary>Raised on the background thread as the 15-second auto-tune progresses.</summary>
    public event EventHandler<CalibrationProgressEventArgs>? CalibrationProgressChanged;

    /// <summary>Raised on the background thread once auto-tune finishes with new parameters.</summary>
    public event EventHandler<CalibrationParameters>? CalibrationCompleted;

    /// <summary>
    /// Raised once, right after the camera opens, with the resolution/FPS the driver actually
    /// negotiated - which does not always match what was requested (see <see cref="Start"/>).
    /// </summary>
    public event EventHandler<string>? ResolutionStatusChanged;

    /// <summary>Raised on the background thread if capture/processing fails unrecoverably.</summary>
    public event EventHandler<Exception>? ErrorOccurred;

    /// <summary>
    /// Opens the given physical camera and starts a new capture session, requesting the given
    /// resolution. Not every camera/driver supports every resolution (4K in particular is far
    /// from universal on webcams) - the driver silently falls back to its closest supported
    /// mode, so the actually negotiated size is read back and reported via
    /// <see cref="ResolutionStatusChanged"/> instead of just assuming the request succeeded.
    /// Returns immediately: opening the device happens on a background thread, and any previous
    /// session is cancelled and guaranteed to fully release the camera before this one opens it.
    /// </summary>
    public void Start(int deviceIndex, int requestedWidth, int requestedHeight)
    {
        _calibrationEngine.Cancel();
        _previewMailbox.Clear();
        _sessions.Start(
            token => RunLoop(deviceIndex, requestedWidth, requestedHeight, token),
            "NeuroCamera.VideoThread",
            ThreadPriority.AboveNormal);
    }

    /// <summary>Stops the current capture session and releases the camera. Does not block.</summary>
    public void Stop()
    {
        _calibrationEngine.Cancel();
        _previewMailbox.Clear();
        _sessions.Stop();
    }

    /// <summary>
    /// Delivers the newest processed frame (if any arrived since the last call) via
    /// <see cref="FrameReady"/>. Intended to be called at the UI's own pace (e.g. from a
    /// CompositionTarget.Rendering or DispatcherTimer tick already on the UI thread) so a slow
    /// UI thread only ever sees the latest frame instead of a growing backlog of stale ones.
    /// </summary>
    public void PumpPreview()
    {
        if (_previewMailbox.TryTake(out BitmapSource? frame))
        {
            FrameReady?.Invoke(this, frame);
        }
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

    /// <summary>
    /// Restores a previously saved calibration result (e.g. from the last session) so
    /// correction is already active without waiting for a fresh 15-second auto-tune.
    /// </summary>
    public void ApplySavedParameters(CalibrationParameters parameters) => _currentParameters = parameters;

    /// <summary>
    /// Sets an always-on brightness nudge (roughly -60..+60) applied on top of whatever the
    /// calibration produced - or, if calibration hasn't run yet, applied on its own - so the
    /// person can brighten or dim the picture instantly without rerunning the full wizard.
    /// </summary>
    public void SetManualBrightnessOffset(double offset) => Volatile.Write(ref _manualBrightnessOffset, offset);

    private void RunLoop(int deviceIndex, int requestedWidth, int requestedHeight, CancellationToken token)
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

            if (token.IsCancellationRequested)
            {
                return;
            }

            if (!capture.IsOpened())
            {
                RaiseError(new InvalidOperationException($"Не удалось открыть камеру с индексом {deviceIndex}."));
                return;
            }

            capture.Set(VideoCaptureProperties.FrameWidth, requestedWidth);
            capture.Set(VideoCaptureProperties.FrameHeight, requestedHeight);
            capture.Set(VideoCaptureProperties.Fps, 30);

            int actualWidth = (int)capture.Get(VideoCaptureProperties.FrameWidth);
            int actualHeight = (int)capture.Get(VideoCaptureProperties.FrameHeight);
            double actualFps = capture.Get(VideoCaptureProperties.Fps);

            string resolutionMessage = (actualWidth == requestedWidth && actualHeight == requestedHeight)
                ? $"Камера работает на {actualWidth}×{actualHeight}, {actualFps:0} к/с."
                : $"Камера не поддерживает {requestedWidth}×{requestedHeight} — используется {actualWidth}×{actualHeight}, {actualFps:0} к/с.";
            ResolutionStatusChanged?.Invoke(this, resolutionMessage);

            using Mat rawFrame = new();

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
                double manualOffset = Volatile.Read(ref _manualBrightnessOffset);

                CalibrationParameters effectiveParameters = manualOffset == 0
                    ? parameters
                    : parameters with { IsCalibrated = true, Beta = parameters.Beta + manualOffset };

                using Mat processedFrame = _imageProcessor.ProcessFrame(rawFrame, effectiveParameters);
                BitmapSource preview = MatImageConverter.ToBitmapSource(processedFrame);
                preview.Freeze();
                _previewMailbox.Post(preview);
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

    private void RaiseError(Exception ex) => ErrorOccurred?.Invoke(this, ex);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _sessions.WaitForIdle(TimeSpan.FromSeconds(5));
        _imageProcessor.Dispose();
        _disposed = true;
    }
}
