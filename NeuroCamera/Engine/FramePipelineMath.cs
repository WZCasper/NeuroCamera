using NeuroCamera.Models;

namespace NeuroCamera.Engine;

/// <summary>
/// Pure (OpenCV-free) math behind the per-frame colour correction, kept separate from
/// <see cref="ImageProcessor"/> so it can be unit-tested without native libraries.
///
/// The correction used to be five full-frame passes (per-channel gain via Split/ConvertTo/Merge,
/// gamma LUT, contrast/brightness, plus copies between them). Every one of those stages is a
/// per-pixel function of a single 8-bit value, so their composition is itself a per-channel
/// function of one byte and can be precomputed once into a 256-entry table, leaving a single
/// cv::LUT pass per frame. The table applies the very same stages, in the same order and with
/// the same rounding/truncation as the passes it replaces, so the picture does not change.
///
/// One detail matters for exactness: OpenCV's Mat.ConvertTo(scale, shift) on 8-bit data narrows
/// scale and shift to float and evaluates value * scale + shift as a single fused multiply-add
/// (v_fma in modules/core/src/convert_scale.simd.hpp), rounded half-to-even. Doing that step in
/// double, or as a separate multiply and add, gives a different level whenever the exact value
/// sits on a rounding tie (e.g. 50 * 0.85 - 30 is 12.5 in a two-step float calculation but
/// 12.5000012 fused, which rounds to 13), so the same fused single-precision arithmetic is used
/// here. MathF.FusedMultiplyAdd is correctly rounded on every CPU, so the table is deterministic.
/// (OpenCV itself is only fused on its vector path, i.e. on CPUs with FMA and for all but the last
/// few pixels of a row; on exact ties elsewhere it can differ by one level. This table matches the
/// vector path, which handles practically every pixel of a real frame.)
/// </summary>
public static class ColorLut
{
    /// <summary>Gamma values closer to 1.0 than this skip the gamma stage entirely (as the old pipeline did).</summary>
    public const double GammaBypassTolerance = 0.005;

    /// <summary>
    /// Builds the 256-entry table for one channel: gain -> gamma -> (alpha, beta).
    /// Stage 1 and 3 round to nearest-even and saturate (what OpenCV's ConvertTo does for 8-bit
    /// data); stage 2 truncates toward zero (what the former LUT's (byte) cast did).
    /// </summary>
    public static byte[] BuildChannelTable(double gain, double gamma, double alpha, double beta)
    {
        byte[] table = new byte[256];

        bool applyGamma = Math.Abs(gamma - 1.0) >= GammaBypassTolerance;
        double inverseGamma = 1.0 / gamma;

        for (int level = 0; level < 256; level++)
        {
            int afterGain = ScaleAndShiftToByte(level, gain, 0.0);

            int afterGamma = applyGamma
                ? TruncateToByte(Math.Pow(afterGain / 255.0, inverseGamma) * 255.0)
                : afterGain;

            table[level] = (byte)ScaleAndShiftToByte(afterGamma, alpha, beta);
        }

        return table;
    }

    /// <summary>
    /// Builds the interleaved 3-channel table (768 bytes: entry i is B(i), G(i), R(i)) that
    /// cv::LUT applies to a BGR frame, from the calibration's white-balance gains, gamma,
    /// contrast (alpha) and brightness (beta).
    /// </summary>
    public static byte[] BuildBgrTable(CalibrationParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        byte[] blue = BuildChannelTable(parameters.AwbGainB, parameters.Gamma, parameters.Alpha, parameters.Beta);
        byte[] green = BuildChannelTable(parameters.AwbGainG, parameters.Gamma, parameters.Alpha, parameters.Beta);
        byte[] red = BuildChannelTable(parameters.AwbGainR, parameters.Gamma, parameters.Alpha, parameters.Beta);

        byte[] interleaved = new byte[256 * 3];
        for (int level = 0; level < 256; level++)
        {
            interleaved[(level * 3) + 0] = blue[level];
            interleaved[(level * 3) + 1] = green[level];
            interleaved[(level * 3) + 2] = red[level];
        }

        return interleaved;
    }

    /// <summary>
    /// value * scale + shift as one fused single-precision operation, rounded half-to-even and
    /// saturated to 0..255 - the arithmetic OpenCV performs for Mat.ConvertTo(-1, scale, shift)
    /// on 8-bit data.
    /// </summary>
    private static int ScaleAndShiftToByte(int value, double scale, double shift)
    {
        float result = MathF.FusedMultiplyAdd(value, (float)scale, (float)shift);

        if (float.IsNaN(result))
        {
            return 0;
        }

        return (int)Math.Clamp(Math.Round((double)result, MidpointRounding.ToEven), 0, 255);
    }

    private static int TruncateToByte(double value)
    {
        if (double.IsNaN(value))
        {
            return 0;
        }

        return (int)Math.Clamp(value, 0, 255);
    }
}

/// <summary>
/// Decides at what size a frame is processed for the live preview. The preview is only ever
/// shown in a window-sized <c>Image</c>, so running the correction (and above all the expensive
/// bilateral filter) on full 1440p/4K frames is wasted work: a 4K bilateral pass alone costs
/// ~4x a 1080p one. Calibration measurements are unaffected - they still use the untouched
/// full-resolution frames.
/// </summary>
public static class PreviewSizing
{
    public const int MaxWidth = 1920;
    public const int MaxHeight = 1080;

    /// <summary>Largest size not exceeding the bounds that keeps the aspect ratio; never upscales.</summary>
    public static (int Width, int Height) Fit(int width, int height, int maxWidth = MaxWidth, int maxHeight = MaxHeight)
    {
        if (width <= 0 || height <= 0 || (width <= maxWidth && height <= maxHeight))
        {
            return (width, height);
        }

        double scale = Math.Min((double)maxWidth / width, (double)maxHeight / height);
        int fittedWidth = Math.Max(1, (int)Math.Round(width * scale, MidpointRounding.ToEven));
        int fittedHeight = Math.Max(1, (int)Math.Round(height * scale, MidpointRounding.ToEven));
        return (fittedWidth, fittedHeight);
    }
}
