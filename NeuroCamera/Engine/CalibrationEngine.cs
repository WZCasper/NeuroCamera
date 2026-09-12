using System.Diagnostics;
using System.Linq;
using NeuroCamera.Models;
using OpenCvSharp;

namespace NeuroCamera.Engine;

/// <summary>
/// Drives the automated 15-second calibration sequence:
///   0-5s  - Robust, face-excluded Gray-World white balance analysis.
///   5-10s - Face detection + skin luminance/contrast/color measurement.
///   10-15s - Sensor noise estimation and bilateral filter tuning.
///
/// The white-balance step deliberately excludes the subject's face from its background
/// color estimate (see <see cref="ImageProcessor.TryComputeBackgroundMeans"/> for why: naive
/// whole-frame Gray-World reads a face's natural warmth as a color cast and pushes skin
/// toward green, especially on dark/low-variety backgrounds). The resulting gains are then
/// damped, tightly clamped, and finally checked against the measured face color so a
/// correction can never push skin outside a plausible tone - it errs toward doing less
/// rather than producing a broken image.
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

    private const double TargetFaceLuminance = 152.0;
    private const double TargetFaceContrastStdDev = 55.0;

    /// <summary>Below this measured (pre-correction) face luminance, software correction alone is fighting a genuine lack of light - see <see cref="CalibrationParameters.SceneWasDark"/>.</summary>
    private const double DarkSceneLuminanceThreshold = 70.0;
    private const double DefaultBilateralSigma = 35.0;

    // How much of the raw Gray-World correction to actually apply (0 = none, 1 = full).
    // Professional AWB implementations rarely fully neutralize a scene; partial correction
    // is a standard mitigation against overcorrection on non-ideal (non-diverse-color) scenes.
    private const double WhiteBalanceDampingFactor = 0.5;

    // Deliberately tight: with exclusion + trimming + damping already reducing risk, a wide
    // clamp is no longer needed, and keeping it tight is the last line of defense against a
    // single-channel runaway like the one this replaced.
    private const double MinGain = 0.8;
    private const double MaxGain = 1.3;

    private readonly ImageProcessor _processor;
    private readonly Stopwatch _stopwatch = new();
    private readonly object _sync = new();

    private readonly List<double> _backgroundMeanB = new();
    private readonly List<double> _backgroundMeanG = new();
    private readonly List<double> _backgroundMeanR = new();

    private readonly List<double> _faceLuminanceSamples = new();
    private readonly List<double> _faceStdDevSamples = new();
    private readonly List<double> _faceMeanB = new();
    private readonly List<double> _faceMeanG = new();
    private readonly List<double> _faceMeanR = new();

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
            _backgroundMeanB.Clear();
            _backgroundMeanG.Clear();
            _backgroundMeanR.Clear();
            _faceLuminanceSamples.Clear();
            _faceStdDevSamples.Clear();
            _faceMeanB.Clear();
            _faceMeanG.Clear();
            _faceMeanR.Clear();
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

            // Track the face across every phase (not just phase 2): phase 1 needs it to
            // exclude skin from the white-balance background estimate, and phase 3 uses it
            // to target the noise estimate at the subject.
            Rect? faceThisFrame = _processor.DetectLargestFace(rawBgrFrame);
            if (faceThisFrame is { } face)
            {
                _lastFaceRect = face;
                _anyFaceDetected = true;
            }

            switch (Phase)
            {
                case CalibrationPhase.WhiteBalance:
                    SampleWhiteBalance(rawBgrFrame, _lastFaceRect);
                    if (elapsed >= Phase1EndSeconds)
                    {
                        Phase = CalibrationPhase.FaceExposure;
                    }
                    break;

                case CalibrationPhase.FaceExposure:
                    if (faceThisFrame is { } exposureFace)
                    {
                        SampleFaceExposure(rawBgrFrame, exposureFace);
                    }
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

    private void SampleWhiteBalance(Mat rawBgrFrame, Rect? faceToExclude)
    {
        if (ImageProcessor.TryComputeBackgroundMeans(rawBgrFrame, faceToExclude, out Scalar means))
        {
            _backgroundMeanB.Add(means.Val0);
            _backgroundMeanG.Add(means.Val1);
            _backgroundMeanR.Add(means.Val2);
        }
        // If there wasn't enough reliable background this frame (e.g. face fills the frame
        // in a pitch-black room), the sample is simply skipped rather than polluting the
        // estimate - BuildFinalParameters() falls back safely if this list ends up empty.
    }

    private void SampleFaceExposure(Mat rawBgrFrame, Rect face)
    {
        (double luminance, double stdDev) = ImageProcessor.MeasureLuminance(rawBgrFrame, face);
        _faceLuminanceSamples.Add(luminance);
        _faceStdDevSamples.Add(stdDev);

        Scalar faceColor = ImageProcessor.MeasureMeanColor(rawBgrFrame, face);
        _faceMeanB.Add(faceColor.Val0);
        _faceMeanG.Add(faceColor.Val1);
        _faceMeanR.Add(faceColor.Val2);
    }

    private void SampleNoise(Mat rawBgrFrame)
    {
        Rect roi = _lastFaceRect ?? CenterRoi(rawBgrFrame.Size());
        double noise = ImageProcessor.EstimateNoiseLevel(rawBgrFrame, roi);
        _noiseSamples.Add(noise);
    }

    private CalibrationParameters BuildFinalParameters()
    {
        (double gainB, double gainG, double gainR) = ComputeWhiteBalanceGains();

        // Fall back to the frame's overall brightness if no face was ever detected, so the
        // exposure step still produces a sensible (if less targeted) correction.
        double fallbackLuminance = (_backgroundMeanB.Count > 0)
            ? (_backgroundMeanB.Average() + _backgroundMeanG.Average() + _backgroundMeanR.Average()) / 3.0
            : 128.0;
        double currentLuminance = _faceLuminanceSamples.Count > 0 ? _faceLuminanceSamples.Average() : fallbackLuminance;
        double currentStdDev = _faceStdDevSamples.Count > 0 ? _faceStdDevSamples.Average() : 45.0;

        double gamma = ImageProcessor.SolveGammaForTargetLuminance(currentLuminance, TargetFaceLuminance);
        double alpha = Math.Clamp(TargetFaceContrastStdDev / Math.Max(currentStdDev, 1.0), 0.85, 1.7);
        double beta = Math.Clamp(TargetFaceLuminance - (currentLuminance * alpha), -45.0, 70.0);

        double avgNoise = _noiseSamples.Count > 0 ? _noiseSamples.Average() : DefaultBilateralSigma;
        int bilateralDiameter = (int)Math.Clamp(Math.Round(5 + (avgNoise / 8.0)), 5, 9);
        double bilateralSigma = Math.Clamp(avgNoise * 1.4, 25.0, 90.0);

        bool sceneWasDark = currentLuminance < DarkSceneLuminanceThreshold;

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
            IsCalibrated = true,
            SceneWasDark = sceneWasDark
        };
    }

    /// <summary>
    /// Turns the accumulated background samples into final white-balance gains: raw
    /// Gray-World gains -> damped -> tightly clamped -> checked against the measured face
    /// color and damped further (or dropped to neutral) if the result would not look like a
    /// plausible skin tone. Every stage only ever pulls the result *toward* neutral (1.0),
    /// never away from it, so the worst case is simply "less correction applied".
    /// </summary>
    private (double GainB, double GainG, double GainR) ComputeWhiteBalanceGains()
    {
        if (_backgroundMeanB.Count == 0)
        {
            // Never found enough reliable, non-face background across the whole 5 seconds -
            // safest option is to not touch white balance at all.
            return (1.0, 1.0, 1.0);
        }

        double meanB = Math.Max(_backgroundMeanB.Average(), 1.0);
        double meanG = Math.Max(_backgroundMeanG.Average(), 1.0);
        double meanR = Math.Max(_backgroundMeanR.Average(), 1.0);
        double grayTarget = (meanB + meanG + meanR) / 3.0;

        double rawGainB = grayTarget / meanB;
        double rawGainG = grayTarget / meanG;
        double rawGainR = grayTarget / meanR;

        double gainB = Math.Clamp(Damp(rawGainB), MinGain, MaxGain);
        double gainG = Math.Clamp(Damp(rawGainG), MinGain, MaxGain);
        double gainR = Math.Clamp(Damp(rawGainR), MinGain, MaxGain);

        if (_faceMeanB.Count == 0)
        {
            return (gainB, gainG, gainR);
        }

        Scalar faceColor = new(_faceMeanB.Average(), _faceMeanG.Average(), _faceMeanR.Average());

        if (ImageProcessor.IsPlausibleSkinTone(faceColor, gainB, gainG, gainR))
        {
            return (gainB, gainG, gainR);
        }

        // The damped/clamped gains would still push the measured face color outside a
        // plausible skin tone - halve the correction once more before trying again.
        gainB = Math.Clamp(1.0 + ((gainB - 1.0) * 0.5), MinGain, MaxGain);
        gainG = Math.Clamp(1.0 + ((gainG - 1.0) * 0.5), MinGain, MaxGain);
        gainR = Math.Clamp(1.0 + ((gainR - 1.0) * 0.5), MinGain, MaxGain);

        return ImageProcessor.IsPlausibleSkinTone(faceColor, gainB, gainG, gainR)
            ? (gainB, gainG, gainR)
            : (1.0, 1.0, 1.0); // still implausible - safest to leave white balance untouched
    }

    private static double Damp(double rawGain) => 1.0 + ((rawGain - 1.0) * WhiteBalanceDampingFactor);

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
        CalibrationPhase.WhiteBalance => "Шаг 1/3: анализ баланса белого (фон, без учёта лица)...",
        CalibrationPhase.FaceExposure => faceDetected
            ? "Шаг 2/3: лицо найдено, измеряю яркость и контраст кожи..."
            : "Шаг 2/3: ищу лицо в кадре — смотрите в камеру...",
        CalibrationPhase.NoiseReduction => "Шаг 3/3: оцениваю шум сенсора и настраиваю сглаживание...",
        CalibrationPhase.Completed => "Автонастройка завершена.",
        _ => string.Empty
    };
}
