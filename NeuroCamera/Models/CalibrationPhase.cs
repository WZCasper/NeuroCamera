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
/// without querying the engine from a different thread.
/// </summary>
public sealed class CalibrationProgressEventArgs : EventArgs
{
    public CalibrationPhase Phase { get; }
    public double ProgressPercent { get; }
    public string StatusMessage { get; }
    public bool FaceDetected { get; }

    public CalibrationProgressEventArgs(CalibrationPhase phase, double progressPercent, string statusMessage, bool faceDetected)
    {
        Phase = phase;
        ProgressPercent = progressPercent;
        StatusMessage = statusMessage;
        FaceDetected = faceDetected;
    }
}
