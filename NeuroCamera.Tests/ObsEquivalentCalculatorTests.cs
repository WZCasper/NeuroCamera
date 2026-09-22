using NeuroCamera.Engine;
using NeuroCamera.Models;
using Xunit;

namespace NeuroCamera.Tests;

public class ObsEquivalentCalculatorTests
{
    [Fact]
    public void Neutral_calibration_maps_to_neutral_obs_values()
    {
        ObsEquivalentCalculator.ObsColorCorrectionValues values =
            ObsEquivalentCalculator.FromCalibration(new CalibrationParameters { IsCalibrated = true });

        Assert.Equal(0.0, values.Gamma, 6);
        Assert.Equal(0.0, values.Contrast, 6);
        Assert.Equal(0.0, values.Brightness, 6);
        Assert.Equal(0.0, values.Saturation, 6);
        Assert.Equal(0.0, values.HueShift, 6);
    }

    // OBS's current Color Correction filter (color_filter_v2) reads "opacity" with
    // obs_data_get_double and multiplies the pixel alpha by it directly; its default is 1.0.
    // Sending the v1-style percentage (100) makes every pixel 100x brighter, i.e. a white picture.
    [Fact]
    public void Opacity_is_expressed_on_the_zero_to_one_scale_of_the_current_obs_filter()
    {
        ObsEquivalentCalculator.ObsColorCorrectionValues values =
            ObsEquivalentCalculator.FromCalibration(new CalibrationParameters { IsCalibrated = true, Gamma = 1.4, Alpha = 1.2, Beta = 12 });

        Assert.Equal(1.0, values.Opacity, 6);
        Assert.Equal(1.0, ObsEquivalentCalculator.NeutralOpacity, 6);
    }

    [Fact]
    public void Known_values_are_inverted_with_the_obs_slider_mappings()
    {
        ObsEquivalentCalculator.ObsColorCorrectionValues brighter =
            ObsEquivalentCalculator.FromCalibration(new CalibrationParameters { Gamma = 2.0, Alpha = 1.25, Beta = 51.0 });
        ObsEquivalentCalculator.ObsColorCorrectionValues darker =
            ObsEquivalentCalculator.FromCalibration(new CalibrationParameters { Gamma = 0.5, Alpha = 0.8, Beta = -51.0 });

        Assert.Equal(1.0, brighter.Gamma, 6);
        Assert.Equal(0.25, brighter.Contrast, 6);
        Assert.Equal(0.2, brighter.Brightness, 6);

        Assert.Equal(-1.0, darker.Gamma, 6);
        Assert.Equal(-0.25, darker.Contrast, 6);
        Assert.Equal(-0.2, darker.Brightness, 6);
    }

    [Fact]
    public void Slider_values_stay_inside_the_ranges_obs_accepts()
    {
        ObsEquivalentCalculator.ObsColorCorrectionValues extreme =
            ObsEquivalentCalculator.FromCalibration(new CalibrationParameters { Gamma = 50.0, Alpha = 50.0, Beta = 10_000.0 });
        ObsEquivalentCalculator.ObsColorCorrectionValues tiny =
            ObsEquivalentCalculator.FromCalibration(new CalibrationParameters { Gamma = 0.0, Alpha = 0.0, Beta = -10_000.0 });

        Assert.InRange(extreme.Gamma, -3.0, 3.0);
        Assert.InRange(extreme.Contrast, -4.0, 4.0);
        Assert.InRange(extreme.Brightness, -1.0, 1.0);
        Assert.InRange(tiny.Gamma, -3.0, 3.0);
        Assert.InRange(tiny.Contrast, -4.0, 4.0);
        Assert.InRange(tiny.Brightness, -1.0, 1.0);
    }

    // Independent model of what OBS's v2 filter does, written from its source
    // (obs-studio/plugins/obs-filters/color-correction-filter.c, color_correction_filter_update_v2
    // and color_correction_filter.effect): pow(pixel, gammaMultiplier), then contrast as a plain
    // multiply, then brightness as an addition (final matrix = contrast x brightness).
    [Fact]
    public void Obs_values_reproduce_the_neurocamera_correction_under_the_obs_v2_model()
    {
        double[] gammas = { 0.7, 0.85, 1.0, 1.2, 1.6, 1.9 };
        double[] alphas = { 0.85, 1.0, 1.15, 1.35 };
        double[] betas = { -40.0, -10.0, 0.0, 15.0, 40.0 };

        foreach (double gamma in gammas)
        {
            foreach (double alpha in alphas)
            {
                foreach (double beta in betas)
                {
                    CalibrationParameters parameters = new() { IsCalibrated = true, Gamma = gamma, Alpha = alpha, Beta = beta };
                    ObsEquivalentCalculator.ObsColorCorrectionValues obs = ObsEquivalentCalculator.FromCalibration(parameters);
                    byte[] neuro = ColorLut.BuildChannelTable(1.0, gamma, alpha, beta);

                    for (int level = 0; level < 256; level++)
                    {
                        double expected = ObsV2Model(level / 255.0, obs) * 255.0;

                        // Tolerance: the app truncates after the gamma stage and rounds at the end
                        // (up to ~1 + 0.5 levels once scaled by contrast), OBS is continuous.
                        Assert.True(
                            Math.Abs(neuro[level] - expected) <= 2.0,
                            $"level {level}: neuro={neuro[level]} obs={expected:F2} (gamma={gamma}, alpha={alpha}, beta={beta})");
                    }
                }
            }
        }
    }

    private static double ObsV2Model(double pixel, ObsEquivalentCalculator.ObsColorCorrectionValues values)
    {
        double gammaMultiplier = values.Gamma < 0.0 ? -values.Gamma + 1.0 : 1.0 / (values.Gamma + 1.0);
        double contrastMultiplier = values.Contrast < 0.0 ? 1.0 / (-values.Contrast + 1.0) : values.Contrast + 1.0;

        double result = (Math.Pow(pixel, gammaMultiplier) * contrastMultiplier) + values.Brightness;
        return Math.Clamp(result, 0.0, 1.0);
    }
}
