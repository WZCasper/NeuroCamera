namespace NeuroCamera.Models;

/// <summary>
/// Everything NeuroCamera remembers between launches: last selected camera, last requested
/// resolution, and the last completed software calibration (so correction is already applied
/// on startup instead of defaulting to neutral until the person reruns Автонастройка).
/// Plain settable properties (not a record) so <see cref="System.Text.Json.JsonSerializer"/>
/// can populate it directly via reflection with no extra configuration.
/// </summary>
public sealed class AppSettings
{
    public string? SelectedCameraName { get; set; }

    public int ResolutionWidth { get; set; } = CaptureResolutions.Default.Width;

    public int ResolutionHeight { get; set; } = CaptureResolutions.Default.Height;

    public CalibrationParameters? LastCalibration { get; set; }
}
