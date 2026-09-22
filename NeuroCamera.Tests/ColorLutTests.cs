using NeuroCamera.Engine;
using NeuroCamera.Models;
using Xunit;

namespace NeuroCamera.Tests;

public class ColorLutTests
{
    // Input grey levels for which the expected outputs below were recorded.
    private static readonly int[] Levels = { 0, 1, 5, 16, 64, 100, 128, 180, 200, 250, 255 };

    // The expected values are NOT produced by the code under test: they were computed with real
    // OpenCV (convertScaleAbs for the gain, LUT for the gamma, addWeighted for alpha/beta - the
    // same fused single-precision arithmetic Mat.ConvertTo uses) on a strip of grey pixels, one
    // strip per parameter set. The same generator was also compared byte for byte with this
    // class over 600 further parameter sets (460,800 table entries) with zero differences.
    [Fact]
    public void Table_matches_the_original_opencv_pipeline_for_set_A()
    {
        AssertMatches(
            new CalibrationParameters { AwbGainB = 1.05, AwbGainG = 1.00, AwbGainR = 0.95, Gamma = 1.25, Alpha = 1.10, Beta = -6.0 },
            blue: new[] { 0, 0, 5, 26, 90, 132, 161, 214, 234, 255, 255 },
            green: new[] { 0, 0, 5, 24, 86, 126, 155, 205, 224, 255, 255 },
            red: new[] { 0, 0, 5, 23, 83, 120, 149, 198, 215, 255, 255 });
    }

    [Fact]
    public void Table_matches_the_original_opencv_pipeline_for_set_B_with_heavy_correction()
    {
        AssertMatches(
            new CalibrationParameters { AwbGainB = 0.90, AwbGainG = 1.10, AwbGainR = 1.30, Gamma = 0.70, Alpha = 1.35, Beta = -40.0 },
            blue: new[] { 0, 0, 0, 0, 1, 37, 69, 140, 169, 248, 255 },
            green: new[] { 0, 0, 0, 0, 14, 63, 107, 199, 238, 255, 255 },
            red: new[] { 0, 0, 0, 0, 29, 91, 146, 255, 255, 255, 255 });
    }

    [Fact]
    public void Table_matches_the_original_opencv_pipeline_when_gamma_is_bypassed()
    {
        AssertMatches(
            new CalibrationParameters { Gamma = 1.0, Alpha = 0.85, Beta = 20.0 },
            blue: new[] { 20, 21, 24, 34, 74, 105, 129, 173, 190, 232, 237 },
            green: new[] { 20, 21, 24, 34, 74, 105, 129, 173, 190, 232, 237 },
            red: new[] { 20, 21, 24, 34, 74, 105, 129, 173, 190, 232, 237 });
    }

    [Fact]
    public void Table_matches_the_original_opencv_pipeline_for_gains_and_strong_gamma_only()
    {
        AssertMatches(
            new CalibrationParameters { AwbGainB = 1.20, AwbGainG = 1.00, AwbGainR = 0.80, Gamma = 1.9, Alpha = 1.0, Beta = 0.0 },
            blue: new[] { 0, 13, 35, 65, 135, 171, 195, 233, 246, 255, 255 },
            green: new[] { 0, 13, 32, 59, 123, 155, 177, 212, 224, 252, 255 },
            red: new[] { 0, 13, 28, 53, 109, 138, 157, 188, 199, 224, 226 });
    }

    // Values that land exactly on x.5 in exact arithmetic are decided by OpenCV's fused single-precision
    // multiply-add, not by round-half-to-even on the exact value. Each expectation below was read from
    // cv2.addWeighted applied to a 256-wide row, i.e. OpenCV's vector path (v_fma), which processes
    // essentially every pixel of a real frame. (A one-element array takes the scalar tail instead, which
    // does not fuse and can land one level lower on exactly these ties - OpenCV itself is path-dependent
    // there, which is why the table pins the fused behaviour and stays deterministic on every CPU.)
    [Fact]
    public void Rounding_ties_follow_opencvs_fused_single_precision_arithmetic()
    {
        Assert.Equal(13, (int)ColorLut.BuildChannelTable(1.0, 1.0, 0.85, -30.0)[50]);   // 50 * 0.85 - 30
        Assert.Equal(120, (int)ColorLut.BuildChannelTable(1.0, 1.0, 1.10, -6.0)[115]);   // 115 * 1.1 - 6
        Assert.Equal(1, (int)ColorLut.BuildChannelTable(1.0, 1.0, 1.35, -40.0)[30]);    // 30 * 1.35 - 40
    }

    [Fact]
    public void Neutral_parameters_produce_the_identity_table()
    {
        byte[] table = ColorLut.BuildBgrTable(new CalibrationParameters());

        for (int level = 0; level < 256; level++)
        {
            Assert.Equal(level, (int)table[(level * 3) + 0]);
            Assert.Equal(level, (int)table[(level * 3) + 1]);
            Assert.Equal(level, (int)table[(level * 3) + 2]);
        }
    }

    [Fact]
    public void Gamma_closer_to_one_than_the_bypass_tolerance_is_ignored()
    {
        byte[] withTinyGamma = ColorLut.BuildChannelTable(1.0, 1.004, 1.0, 0.0);

        for (int level = 0; level < 256; level++)
        {
            Assert.Equal(level, (int)withTinyGamma[level]);
        }
    }

    [Fact]
    public void Bgr_table_is_interleaved_blue_green_red_and_768_bytes_long()
    {
        CalibrationParameters parameters = new() { AwbGainB = 1.2, AwbGainG = 1.0, AwbGainR = 0.8, Gamma = 1.3, Alpha = 1.1, Beta = 5.0 };

        byte[] table = ColorLut.BuildBgrTable(parameters);
        byte[] blue = ColorLut.BuildChannelTable(1.2, 1.3, 1.1, 5.0);
        byte[] green = ColorLut.BuildChannelTable(1.0, 1.3, 1.1, 5.0);
        byte[] red = ColorLut.BuildChannelTable(0.8, 1.3, 1.1, 5.0);

        Assert.Equal(768, table.Length);
        for (int level = 0; level < 256; level++)
        {
            Assert.Equal((int)blue[level], (int)table[(level * 3) + 0]);
            Assert.Equal((int)green[level], (int)table[(level * 3) + 1]);
            Assert.Equal((int)red[level], (int)table[(level * 3) + 2]);
        }
    }

    [Fact]
    public void Every_channel_table_is_monotonic_for_positive_gain_and_contrast()
    {
        Random random = new(12345);

        for (int trial = 0; trial < 200; trial++)
        {
            double gain = 0.8 + (random.NextDouble() * 0.5);
            double gamma = 0.65 + (random.NextDouble() * 1.25);
            double alpha = 0.85 + (random.NextDouble() * 0.5);
            double beta = -80.0 + (random.NextDouble() * 160.0);

            byte[] table = ColorLut.BuildChannelTable(gain, gamma, alpha, beta);

            for (int level = 1; level < 256; level++)
            {
                Assert.True(
                    table[level] >= table[level - 1],
                    $"Not monotonic at {level} for gain={gain}, gamma={gamma}, alpha={alpha}, beta={beta}.");
            }
        }
    }

    [Fact]
    public void Extreme_parameters_saturate_instead_of_wrapping_around()
    {
        byte[] bright = ColorLut.BuildChannelTable(1.3, 1.0, 1.35, 80.0);
        byte[] dark = ColorLut.BuildChannelTable(0.8, 1.0, 0.85, -80.0);

        Assert.Equal(255, (int)bright[255]);
        Assert.Equal(0, (int)dark[0]);
        Assert.Equal(0, (int)dark[50]);
    }

    private static void AssertMatches(CalibrationParameters parameters, int[] blue, int[] green, int[] red)
    {
        byte[] table = ColorLut.BuildBgrTable(parameters);

        for (int i = 0; i < Levels.Length; i++)
        {
            int level = Levels[i];
            Assert.Equal(blue[i], (int)table[(level * 3) + 0]);
            Assert.Equal(green[i], (int)table[(level * 3) + 1]);
            Assert.Equal(red[i], (int)table[(level * 3) + 2]);
        }
    }
}

public class PreviewSizingTests
{
    [Fact]
    public void Frames_within_the_limit_are_left_alone()
    {
        Assert.Equal((640, 480), PreviewSizing.Fit(640, 480));
        Assert.Equal((1280, 720), PreviewSizing.Fit(1280, 720));
        Assert.Equal((1920, 1080), PreviewSizing.Fit(1920, 1080));
    }

    [Fact]
    public void Four_k_is_halved_to_full_hd()
    {
        Assert.Equal((1920, 1080), PreviewSizing.Fit(3840, 2160));
    }

    [Fact]
    public void Qhd_is_scaled_down_to_full_hd()
    {
        Assert.Equal((1920, 1080), PreviewSizing.Fit(2560, 1440));
    }

    [Fact]
    public void Ultrawide_keeps_its_aspect_ratio()
    {
        (int width, int height) = PreviewSizing.Fit(2560, 1080);

        Assert.Equal(1920, width);
        Assert.Equal(810, height);
    }

    [Fact]
    public void Portrait_frames_are_limited_by_height()
    {
        (int width, int height) = PreviewSizing.Fit(1080, 1920);

        Assert.Equal(1080, height);
        Assert.Equal(608, width);
    }

    [Fact]
    public void Result_never_exceeds_the_bounds_or_upscales()
    {
        int[] widths = { 160, 640, 1280, 1920, 2560, 3840, 5120, 7680 };
        int[] heights = { 120, 480, 720, 1080, 1440, 2160, 2880, 4320 };

        foreach (int width in widths)
        {
            foreach (int height in heights)
            {
                (int fittedWidth, int fittedHeight) = PreviewSizing.Fit(width, height);

                Assert.True(fittedWidth <= Math.Max(width, 1) && fittedHeight <= Math.Max(height, 1), $"upscaled {width}x{height}");
                Assert.True(fittedWidth <= PreviewSizing.MaxWidth || width <= PreviewSizing.MaxWidth, $"too wide for {width}x{height}");
                Assert.True(fittedHeight <= PreviewSizing.MaxHeight || height <= PreviewSizing.MaxHeight, $"too tall for {width}x{height}");
                Assert.True(fittedWidth >= 1 && fittedHeight >= 1, $"empty result for {width}x{height}");
            }
        }
    }

    [Fact]
    public void Degenerate_sizes_are_returned_unchanged()
    {
        Assert.Equal((0, 0), PreviewSizing.Fit(0, 0));
        Assert.Equal((-5, 10), PreviewSizing.Fit(-5, 10));
    }
}
