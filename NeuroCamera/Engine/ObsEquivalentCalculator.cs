using NeuroCamera.Models;

namespace NeuroCamera.Engine;

/// <summary>
/// Converts NeuroCamera's own software-correction parameters into the equivalent values for
/// OBS's built-in "Color Correction" filter (internal id <c>color_filter</c>), derived
/// directly from that filter's real source
/// (obs-studio/plugins/obs-filters/color-correction-filter.c, the current "v2" update
/// function):
///
///   OBS internally turns its Gamma slider value g into a multiplier applied as
///   pow(pixel, obsGammaMultiplier):
///     obsGammaMultiplier = (g &lt; 0) ? (-g + 1) : (1 / (g + 1))
///   NeuroCamera's own gamma is applied as pow(pixel, 1/gamma). Solving
///   obsGammaMultiplier == 1/gamma for the slider value g gives:
///     g = gamma - 1              when gamma &gt;= 1
///     g = 1 - 1/gamma            when gamma &lt;  1
///
///   OBS turns its Contrast slider value c into a multiplier:
///     obsContrastMultiplier = (c &lt; 0) ? (1 / (1 - c)) : (c + 1)
///   NeuroCamera's alpha is used directly as a multiplier, so the same inversion applies:
///     c = alpha - 1              when alpha &gt;= 1
///     c = 1 - 1/alpha            when alpha &lt;  1
///
///   OBS's Brightness slider is an additive offset in normalized 0-1 color space.
///   NeuroCamera's beta is the same kind of additive offset in 0-255 pixel-value space:
///     obsBrightness = beta / 255
///
/// NeuroCamera does not adjust saturation or hue, so those are reported as OBS's own neutral
/// defaults (0). White balance (the AWB gains) has no single-slider equivalent in OBS's Color
/// Correction filter and is intentionally left out rather than guessed at.
///
/// This is a close, source-derived approximation of visual equivalence, not a guaranteed
/// pixel-identical match: OBS composes contrast/brightness/saturation/hue as one 4x4 color
/// matrix multiply, while NeuroCamera applies gamma/contrast/brightness as separate sequential
/// steps, so the two can diverge slightly at extreme values. Treat the numbers as an accurate
/// starting point to fine-tune visually, not a guaranteed exact match.
/// </summary>
public static class ObsEquivalentCalculator
{
    public readonly record struct ObsColorCorrectionValues(
        double Gamma,
        double Contrast,
        double Brightness,
        double Saturation,
        double HueShift,
        double Opacity);

    public static ObsColorCorrectionValues FromCalibration(CalibrationParameters parameters)
    {
        double obsGamma = Math.Clamp(InvertObsStyle(parameters.Gamma), -3.0, 3.0);
        double obsContrast = Math.Clamp(InvertObsStyle(parameters.Alpha), -4.0, 4.0);
        double obsBrightness = Math.Clamp(parameters.Beta / 255.0, -1.0, 1.0);

        return new ObsColorCorrectionValues(obsGamma, obsContrast, obsBrightness, Saturation: 0.0, HueShift: 0.0, Opacity: 1.0);
    }

    /// <summary>
    /// Inverts OBS's "(x &lt; 0) ? (1/(1-x)) : (x+1)"-style multiplier mapping (the same shape
    /// is used for both its Gamma and Contrast sliders) to solve for the slider value that
    /// produces a given target multiplier.
    /// </summary>
    private static double InvertObsStyle(double myMultiplier)
    {
        double safeMultiplier = Math.Max(myMultiplier, 0.01);
        return safeMultiplier >= 1.0 ? (safeMultiplier - 1.0) : (1.0 - (1.0 / safeMultiplier));
    }
}
