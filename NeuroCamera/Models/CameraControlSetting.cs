namespace NeuroCamera.Models;

/// <summary>Which DirectShow interface a <see cref="CameraControlSetting"/> comes from.</summary>
public enum CameraControlKind
{
    /// <summary>From IAMVideoProcAmp (brightness, contrast, hue, saturation, sharpness, gamma, white balance, backlight, gain).</summary>
    VideoProcAmp,

    /// <summary>From IAMCameraControl (pan, tilt, roll, zoom, exposure, iris, focus).</summary>
    CameraControl
}

/// <summary>
/// Immutable snapshot of one hardware camera property as reported by the driver itself
/// (via IAMVideoProcAmp::GetRange/Get or IAMCameraControl::GetRange/Get) - the exact same
/// values and ranges OBS's own camera properties dialog reads, since both go through the
/// same DirectShow interfaces.
/// </summary>
public sealed record CameraControlSetting(
    CameraControlKind Kind,
    int PropertyIndex,
    string DisplayName,
    int Min,
    int Max,
    int Step,
    int DefaultValue,
    bool SupportsAuto,
    int CurrentValue,
    bool CurrentIsAuto);
