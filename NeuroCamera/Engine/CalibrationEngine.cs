using System.Diagnostics;
using System.Linq;
using NeuroCamera.Models;
using OpenCvSharp;

namespace NeuroCamera.Engine;

/// <summary>
/// Drives the automated 15-second calibration sequence:
///   0-5s  - Gray-World white balance analysis over the full frame.
///   5-10s - Face detection + skin luminance/contrast measurement.
///   10-15s - Sensor noise estimation and bilateral filter tuning.
///
/// <see cref="FeedFrame"/> must be called once per captured frame from the same thread that
/// owns the video capture loop (never the UI thread) while a calibration is running. Progress
/// is timed with a <see cref="Stopwatch"/> rather than a frame counter, so results are
/// consistent regardless of the camera's actual frame rate.
/// </summary>
public sealed class CalibrationEngine
{
    private const double Phase1EndSeconds = 5.0;
    private const double Phase2EndSeconds = 10.0;
    private const double Phase3EndSeconds = 15.0;

    private const double TargetFaceLuminance = 132.0;
    private const double TargetFaceContrastStdDev = 55.0;
    private const double DefaultBilateralSigma = 35.0;

    private readonly ImageProcessor _processor;
    private readonly Stopwatch _stopwatch = new();
    private readonly object _sync = new();

    private readonly List<double> _frameMeanB = new();
    private readonly List<double> _frameMeanG = new();
    private readonly List<double> _frameMeanR = new();

    private readonly List<double> _faceLuminanceSamples = new();
    private readonly List<double> _faceStdDevSamples = new();

    private readonly List<double> _noiseSamples = new();

    private Rect? _lastFaceRect;
    private bool _anyFaceDetected;

    public CalibrationEngine(ImageProcessor processor)
    {
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
    }

    public CalibrationPhase Phase { get; private set; } = CalibrationPhase.Idle;

    public bool IsRunning =>
        Phase is CalibrationPhase.WhiteBalance or CalibrationPhase.FaceExposure or CalibrationPhase.NoiseReduction;

    /// <summary>Raised on every fed frame while running, with progress/status for the UI.</summary>
    public event EventHandler<CalibrationProgressEventArgs>? ProgressChanged;

    /// <summary>Raised exactly once, when the 15 seconds elapse and new parameters are ready.</summary>
    public event EventHandler<CalibrationParameters>? Completed;

    /// <summary>Begins a new 15-second calibration pass, discarding any previous samples.</summary>
    public void Start()
    {
        lock (_sync)
        {
            _frameMeanB.Clear();
            _frameMeanG.Clear();
            _frameMeanR.Clear();
            _faceLuminanceSamples.Clear();
            _faceStdDevSamples.Clear();
            _noiseSamples.Clear();
            _lastFaceRect = null;
            _anyFaceDetected = false;

            Phase = CalibrationPhase.WhiteBalance;
            _stopwatch.Restart();
        }

        ProgressChanged?.Invoke(this, new CalibrationProgressEventArgs(
            CalibrationPhase.WhiteBalance, 0.0, BuildStatusMessage(CalibrationPhase.WhiteBalance, false), false));
    }

    /// <summary>Aborts a running calibration without publishing new parameters.</summary>
    public void Cancel()
    {
        lock (_sync)
        {
            if (!IsRunning)
            {
                return;
            }

            Phase = CalibrationPhase.Idle;
            _stopwatch.Reset();
        }

        ProgressChanged?.Invoke(this, new CalibrationProgressEventArgs(CalibrationPhase.Idle, 0.0, "Автонастройка отменена.", false));
    }

    /// <summary>
    /// Feeds one raw (not yet processed) BGR frame into the running calibration. No-op when
    /// no calibration is in progress.
    /// </summary>
    public void FeedFrame(Mat rawBgrFrame)
    {
        if (!IsRunning || rawBgrFrame.Empty())
        {
            return;
        }

        CalibrationParameters? completedParameters = null;
        CalibrationPhase phaseForEvent;
        double elapsedForEvent;

        lock (_sync)
        {
            double elapsed = _stopwatch.Elapsed.TotalSeconds;

            switch (Phase)
            {
                case CalibrationPhase.WhiteBalance:
                    SampleWhiteBalance(rawBgrFrame);
                    if (elapsed >= Phase1EndSeconds)
                    {
                        Phase = CalibrationPhase.FaceExposure;
                    }
                    break;

                case CalibrationPhase.FaceExposure:
                    SampleFaceExposure(rawBgrFrame);
                    if (elapsed >= Phase2EndSeconds)
                    {
                        Phase = CalibrationPhase.NoiseReduction;
                    }
                    break;

                case CalibrationPhase.NoiseReduction:
                    SampleNoise(rawBgrFrame);
                    if (elapsed >= Phase3EndSeconds)
                    {
                        completedParameters = BuildFinalParameters();
                        Phase = CalibrationPhase.Completed;
                    }
                    break;
            }

            elapsedForEvent = Math.Min(elapsed, Phase3EndSeconds);
            phaseForEvent = Phase;
        }

        if (completedParameters is not null)
        {
            ProgressChanged?.Invoke(this, new CalibrationProgressEventArgs(
                CalibrationPhase.Completed, 100.0, "Автонастройка завершена.", _anyFaceDetected));
            Completed?.Invoke(this, completedParameters);
            return;
        }

        double progressPercent = elapsedForEvent / Phase3EndSeconds * 100.0;
        ProgressChanged?.Invoke(this, new CalibrationProgressEventArgs(
            phaseForEvent, progressPercent, BuildStatusMessage(phaseForEvent, _anyFaceDetected), _anyFaceDetected));
    }

    private void SampleWhiteBalance(Mat rawBgrFrame)
    {
        Cv2.MeanStdDev(rawBgrFrame, out Scalar mean, out _);
        _frameMeanB.Add(mean.Val0);
        _frameMeanG.Add(mean.Val1);
        _frameMeanR.Add(mean.Val2);
    }

    private void SampleFaceExposure(Mat rawBgrFrame)
    {
        Rect? face = _processor.DetectLargestFace(rawBgrFrame);
        if (face is null)
        {
            return;
        }

        _lastFaceRect = face;
        _anyFaceDetected = true;

        (double luminance, double stdDev) = ImageProcessor.MeasureLuminance(rawBgrFrame, face.Value);
        _faceLuminanceSamples.Add(luminance);
        _faceStdDevSamples.Add(stdDev);
    }

    private void SampleNoise(Mat rawBgrFrame)
    {
        Rect roi = _lastFaceRect ?? CenterRoi(rawBgrFrame.Size());
        double noise = ImageProcessor.EstimateNoiseLevel(rawBgrFrame, roi);
        _noiseSamples.Add(noise);
    }

    private CalibrationParameters BuildFinalParameters()
    {
        double avgMeanB = _frameMeanB.Count > 0 ? _frameMeanB.Average() : 128.0;
        double avgMeanG = _frameMeanG.Count > 0 ? _frameMeanG.Average() : 128.0;
        double avgMeanR = _frameMeanR.Count > 0 ? _frameMeanR.Average() : 128.0;
        double grayTarget = (avgMeanB + avgMeanG + avgMeanR) / 3.0;

        double gainB = Math.Clamp(grayTarget / Math.Max(avgMeanB, 1.0), 0.5, 2.2);
        double gainG = Math.Clamp(grayTarget / Math.Max(avgMeanG, 1.0), 0.5, 2.2);
        double gainR = Math.Clamp(grayTarget / Math.Max(avgMeanR, 1.0), 0.5, 2.2);

        // Fall back to the frame's overall brightness if no face was ever detected, so the
        // exposure step still produces a sensible (if less targeted) correction.
        double currentLuminance = _faceLuminanceSamples.Count > 0 ? _faceLuminanceSamples.Average() : grayTarget;
        double currentStdDev = _faceStdDevSamples.Count > 0 ? _faceStdDevSamples.Average() : 45.0;

        double gamma = ImageProcessor.SolveGammaForTargetLuminance(currentLuminance, TargetFaceLuminance);
        double alpha = Math.Clamp(TargetFaceContrastStdDev / Math.Max(currentStdDev, 1.0), 0.85, 1.6);
        double beta = Math.Clamp(TargetFaceLuminance - (currentLuminance * alpha), -60.0, 60.0);

        double avgNoise = _noiseSamples.Count > 0 ? _noiseSamples.Average() : DefaultBilateralSigma;
        int bilateralDiameter = (int)Math.Clamp(Math.Round(5 + (avgNoise / 8.0)), 5, 9);
        double bilateralSigma = Math.Clamp(avgNoise * 1.4, 25.0, 90.0);

        return new CalibrationParameters
        {
            AwbGainB = gainB,
            AwbGainG = gainG,
            AwbGainR = gainR,
            Gamma = gamma,
            Alpha = alpha,
            Beta = beta,
            BilateralDiameter = bilateralDiameter,
            BilateralSigmaColor = bilateralSigma,
            BilateralSigmaSpace = bilateralSigma,
            IsCalibrated = true
        };
    }

    private static Rect CenterRoi(Size frameSize)
    {
        int width = Math.Max(1, frameSize.Width / 2);
        int height = Math.Max(1, frameSize.Height / 2);
        int x = frameSize.Width / 4;
        int y = frameSize.Height / 4;
        return new Rect(x, y, width, height);
    }

    private static string BuildStatusMessage(CalibrationPhase phase, bool faceDetected) => phase switch
    {
        CalibrationPhase.WhiteBalance => "Шаг 1/3: анализ баланса белого по всему кадру...",
        CalibrationPhase.FaceExposure => faceDetected
            ? "Шаг 2/3: лицо найдено, измеряю яркость и контраст кожи..."
            : "Шаг 2/3: ищу лицо в кадре — смотрите в камеру...",
        CalibrationPhase.NoiseReduction => "Шаг 3/3: оцениваю шум сенсора и настраиваю сглаживание...",
        CalibrationPhase.Completed => "Автонастройка завершена.",
        _ => string.Empty
    };
}
