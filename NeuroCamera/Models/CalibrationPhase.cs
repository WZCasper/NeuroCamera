namespace NeuroCamera.Models;

/// <summary>
/// The state machine steps of the automated 15-second calibration routine.
/// </summary>
public enum CalibrationPhase
{
    /// <summary>Calibration has not started, or has finished and been reset.</summary>
    Idle,

    /// <summary>0-5s: Gray-World auto white balance analysis over the full frame.</summary>
    WhiteBalance,

    /// <summary>5-10s: Face detection and skin luminance measurement for gamma/contrast.</summary>
    FaceExposure,

    /// <summary>10-15s: Noise estimation and bilateral filter tuning.</summary>
    NoiseReduction,

    /// <summary>Calibration finished and new <see cref="CalibrationParameters"/> published.</summary>
    Completed
}

/// <summary>
/// Raised by <see cref="NeuroCamera.Engine.CalibrationEngine"/> as calibration progresses,
/// carrying enough information for the UI to render the progress bar and instruction text
/// without querying the engine from a different thread. Also carries the current frame's raw
/// measurements (when available) so a UI-thread hardware controller can drive real camera
/// sliders in step with the same calibration timeline, instead of the software correction and
/// any hardware adjustment working from different/inconsistent data.
/// </summary>
public sealed class CalibrationProgressEventArgs : EventArgs
{
    public CalibrationPhase Phase { get; }
    public double ProgressPercent { get; }
    public string StatusMessage { get; }
    public bool FaceDetected { get; }

    /// <summary>Current frame's measured face luminance (0-255), when a face is detected during the exposure phase.</summary>
    public double? FaceLuminance { get; }

    /// <summary>Current frame's measured noise level (Laplacian std-dev), when available during the noise-reduction phase.</summary>
    public double? NoiseLevel { get; }

    public CalibrationProgressEventArgs(
        CalibrationPhase phase,
        double progressPercent,
        string statusMessage,
        bool faceDetected,
        double? faceLuminance = null,
        double? noiseLevel = null)
    {
        Phase = phase;
        ProgressPercent = progressPercent;
        StatusMessage = statusMessage;
        FaceDetected = faceDetected;
        FaceLuminance = faceLuminance;
        NoiseLevel = noiseLevel;
    }
}
