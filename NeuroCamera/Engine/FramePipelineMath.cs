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

/// <summary>
/// Guards that keep <see cref="CalibrationEngine"/>'s otherwise face-targeted math from
/// producing a technically-on-target-for-the-face but visibly wrong result elsewhere in the
/// frame. Kept here, alongside <see cref="ColorLut"/> and <see cref="PreviewSizing"/>, so this
/// reasoning is unit-testable without OpenCV; <see cref="CalibrationEngine"/> calls into it.
/// </summary>
public static class CalibrationSafeguards
{
    /// <summary>Background color saturation at or below this is an ordinary mixed-color room - full trust in Gray-World.</summary>
    public const double BackgroundSaturationFullTrust = 0.20;

    /// <summary>Background color saturation at or above this (a strongly colored backdrop) means Gray-World has nothing reliable to go on - zero trust, gains pulled fully to neutral.</summary>
    public const double BackgroundSaturationNoTrust = 0.55;

    /// <summary>
    /// A representative dim-background input level (0-255) used by
    /// <see cref="RaiseBetaToProtectShadows"/> to check whether Alpha/Beta introduce extra
    /// darkening beyond what Gamma alone already does at that level.
    /// </summary>
    public const double ShadowFloorInputLevel = 20.0;

    /// <summary>
    /// How many output levels darker than "Gamma alone, no Alpha/Beta" the shadow floor is
    /// allowed to end up once Alpha/Beta are applied too - see <see cref="RaiseBetaToProtectShadows"/>.
    /// </summary>
    public const double ShadowFloorMaxExtraDarkening = 8.0;

    /// <summary>
    /// How far <see cref="RaiseBetaToProtectShadows"/> is allowed to move Beta away from what
    /// <see cref="CalibrationEngine"/> originally solved for the face target. A large solved
    /// Beta (very negative, from a low-contrast face) sits in the steep, non-linear part of the
    /// gamma-compressed shadow region, where fully protecting the floor can demand a much bigger
    /// swing than the actual problem warrants; capping the swing keeps the guard from trading an
    /// unbounded amount of face-target accuracy for shadow protection; in the range this
    /// otherwise leaves unprotected, it still improves the floor as far as this movement allows.
    /// </summary>
    public const double ShadowFloorMaxBetaMovement = 20.0;

    /// <summary>
    /// How far a background's mean BGR color sits from neutral gray, as a 0.0 (perfectly
    /// neutral) to 1.0 (fully saturated toward one channel) fraction: the largest per-channel
    /// deviation from the mean of the three channels, relative to that mean. Gray-World white
    /// balance implicitly assumes this is close to 0 (a background with a mix of colors that
    /// average out to gray); a single strongly colored backdrop - a streaming curtain, colored
    /// LED lighting - breaks that assumption outright, which is exactly what this measures.
    /// </summary>
    public static double BackgroundSaturation(double meanB, double meanG, double meanR)
    {
        double mean = (meanB + meanG + meanR) / 3.0;
        if (mean < 1.0)
        {
            return 0.0;
        }

        double maxDeviation = Math.Max(Math.Abs(meanB - mean), Math.Max(Math.Abs(meanG - mean), Math.Abs(meanR - mean)));
        return Math.Clamp(maxDeviation / mean, 0.0, 1.0);
    }

    /// <summary>
    /// How much a raw Gray-World gain should actually be trusted, given how saturated the
    /// background looks: 1.0 (full trust, unchanged) at or below
    /// <see cref="BackgroundSaturationFullTrust"/>, ramping linearly down to 0.0 (no trust -
    /// gain forced to neutral) at or above <see cref="BackgroundSaturationNoTrust"/>. A raw gain
    /// is blended toward 1.0 by this factor - see <see cref="CalibrationEngine"/> - so a
    /// strongly colored backdrop (e.g. a red streaming curtain) can no longer be misread as a
    /// color cast on the subject's skin the way naive Gray-World would read it.
    /// </summary>
    public static double BackgroundColorTrust(double meanB, double meanG, double meanR)
    {
        double saturation = BackgroundSaturation(meanB, meanG, meanR);

        if (saturation <= BackgroundSaturationFullTrust)
        {
            return 1.0;
        }

        if (saturation >= BackgroundSaturationNoTrust)
        {
            return 0.0;
        }

        return 1.0 - ((saturation - BackgroundSaturationFullTrust) / (BackgroundSaturationNoTrust - BackgroundSaturationFullTrust));
    }

    /// <summary>
    /// Gamma/Alpha/Beta are solved from the face's own measured brightness/contrast alone and
    /// then applied to the whole frame. When the face has low contrast (common under soft,
    /// diffuse light), Alpha climbs toward its maximum and the Beta needed to keep the face mean
    /// on target goes correspondingly very negative - which, applied everywhere, also crushes
    /// anything darker than the face (most visibly a dim background) toward black, even though
    /// nothing about the background asked for that.
    ///
    /// The comparison is deliberately relative, not an absolute output floor: it checks how much
    /// *additional* darkening Alpha/Beta introduce at <see cref="ShadowFloorInputLevel"/> beyond
    /// what Gamma alone already produces there (Gamma's own job - bringing an under- or
    /// over-exposed face to target - is left alone; only Alpha/Beta's extra contrast/brightness
    /// stretch is checked). An absolute floor would keep nudging an already-dark, untouched scene
    /// brighter even when Alpha=1 and Beta=0 - i.e. when nothing was actually being corrected.
    ///
    /// Uses <see cref="ColorLut.BuildChannelTable"/> with gain fixed at 1.0 - the exact same
    /// table math <see cref="ImageProcessor.ProcessFrame"/> applies to a real frame, since this
    /// is about the exposure curve, not white balance. Only ever raises Beta, never lowers it,
    /// and never moves it by more than <see cref="ShadowFloorMaxBetaMovement"/> - see that
    /// constant's own doc comment for why that cap exists. If the floor is not threatened at
    /// all, Beta is returned completely unchanged; if it is threatened but nothing within the
    /// movement cap measurably improves it (possible when Alpha&lt;1 keeps compressing the floor
    /// back down about as fast as Beta lifts it), Beta is likewise returned unchanged rather
    /// than moved for no benefit - the guard never spends movement without a payoff.
    ///
    /// This is meant to be called exactly once, with the Beta <see cref="CalibrationEngine"/>'s
    /// own face-targeting math just solved (as its one call site does) - not repeatedly on its
    /// own output. The movement cap is relative to whatever Beta is passed in, so calling this
    /// again on an already-adjusted Beta grants a fresh movement budget rather than respecting
    /// the original cap; that is by design for the single-call use this exists for, not a
    /// contract this function guarantees under repeated application.
    /// </summary>
    public static double RaiseBetaToProtectShadows(double gamma, double alpha, double beta)
    {
        int floorLevel = (int)Math.Round(ShadowFloorInputLevel);

        byte[] gammaOnlyTable = ColorLut.BuildChannelTable(1.0, gamma, 1.0, 0.0);
        double minAllowed = Math.Max(0.0, gammaOnlyTable[floorLevel] - ShadowFloorMaxExtraDarkening);

        byte[] currentTable = ColorLut.BuildChannelTable(1.0, gamma, alpha, beta);
        double startingFloor = currentTable[floorLevel];
        if (startingFloor >= minAllowed)
        {
            return beta;
        }

        // Fell short - search upward (in whole levels, the table's own granularity) for the
        // smallest beta increase that clears minAllowed, never past the movement cap or the
        // same +-80 range every other Beta value is clamped to. Tracks the best candidate seen
        // so far so that, if the movement cap is reached before minAllowed is cleared, this
        // still returns whichever tried beta improved the floor the most - and, if nothing in
        // range improves on the unmodified starting point at all (possible when Alpha<1 keeps
        // compressing the floor back down as fast as Beta lifts it), returns beta completely
        // unchanged rather than moving it for no measurable benefit.
        double betaCeiling = Math.Min(80.0, beta + ShadowFloorMaxBetaMovement);
        double bestBeta = beta;
        double bestFloor = startingFloor;

        for (double candidate = beta + 1.0; candidate <= betaCeiling; candidate += 1.0)
        {
            byte[] candidateTable = ColorLut.BuildChannelTable(1.0, gamma, alpha, candidate);
            double candidateFloor = candidateTable[floorLevel];

            if (candidateFloor >= minAllowed)
            {
                return candidate;
            }

            if (candidateFloor > bestFloor)
            {
                bestFloor = candidateFloor;
                bestBeta = candidate;
            }
        }

        return bestBeta;
    }
}
