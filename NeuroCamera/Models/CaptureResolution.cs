namespace NeuroCamera.Models;

/// <summary>A capture resolution the user can request from the physical camera.</summary>
public sealed record CaptureResolution(int Width, int Height, string DisplayName)
{
    public override string ToString() => DisplayName;
}

/// <summary>Common resolution presets offered in the UI, from SD up to 4K UHD.</summary>
public static class CaptureResolutions
{
    public static IReadOnlyList<CaptureResolution> Presets { get; } = new[]
    {
        new CaptureResolution(640, 480, "640 × 480 (SD)"),
        new CaptureResolution(1280, 720, "1280 × 720 (HD)"),
        new CaptureResolution(1920, 1080, "1920 × 1080 (Full HD)"),
        new CaptureResolution(2560, 1440, "2560 × 1440 (QHD)"),
        new CaptureResolution(3840, 2160, "3840 × 2160 (4K UHD)"),
    };

    public static CaptureResolution Default => Presets[1]; // 1280x720

    /// <summary>Finds a preset matching the given dimensions, or <see cref="Default"/> if none match (e.g. an old saved setting no longer in the list).</summary>
    public static CaptureResolution FindOrDefault(int width, int height)
    {
        foreach (CaptureResolution preset in Presets)
        {
            if (preset.Width == width && preset.Height == height)
            {
                return preset;
            }
        }

        return Default;
    }
}
