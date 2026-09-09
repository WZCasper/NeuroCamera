using System.Runtime.InteropServices;
using NeuroCamera.Models;
using OpenCvSharp;

namespace NeuroCamera.Engine;

/// <summary>
/// All pixel-level OpenCV math for NeuroCamera: Gray-World white balance, Haar-cascade face
/// detection, gamma/contrast exposure correction and bilateral denoising. Every public method
/// that returns a <see cref="Mat"/> hands ownership of a brand-new Mat to the caller, who is
/// responsible for disposing it - none of these methods dispose or mutate the Mat you pass in
/// (frames are cloned defensively where needed) so the caller's original frame stays valid.
/// </summary>
public sealed class ImageProcessor : IDisposable
{
    private readonly CascadeClassifier _faceCascade;
    private bool _disposed;

    public ImageProcessor(string haarCascadePath)
    {
        if (string.IsNullOrWhiteSpace(haarCascadePath) || !File.Exists(haarCascadePath))
        {
            throw new FileNotFoundException("Haar cascade XML not found.", haarCascadePath);
        }

        _faceCascade = new CascadeClassifier(haarCascadePath);
    }

    /// <summary>
    /// Detects faces in a BGR frame and returns the largest one (assumed to be the primary
    /// subject), or null if no face is currently visible.
    /// </summary>
    public Rect? DetectLargestFace(Mat bgrFrame)
    {
        using Mat gray = new();
        Cv2.CvtColor(bgrFrame, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.EqualizeHist(gray, gray);

        Size minSize = new(Math.Max(24, gray.Width / 8), Math.Max(24, gray.Height / 8));

        Rect[] faces = _faceCascade.DetectMultiScale(
            gray,
            scaleFactor: 1.1,
            minNeighbors: 5,
            flags: HaarDetectionTypes.ScaleImage,
            minSize: minSize);

        if (faces.Length == 0)
        {
            return null;
        }

        Rect largest = faces[0];
        foreach (Rect face in faces)
        {
            if (face.Width * face.Height > largest.Width * largest.Height)
            {
                largest = face;
            }
        }

        return largest;
    }

    /// <summary>
    /// Computes per-channel Gray-World white balance gains for a full BGR frame: assumes the
    /// scene average should be neutral gray and derives multipliers that push each channel's
    /// mean toward the overall gray average. Gains are clamped to avoid extreme color casts
    /// on scenes that are naturally dominated by one color.
    /// </summary>
    public static (double GainB, double GainG, double GainR) ComputeGrayWorldGains(Mat bgrFrame)
    {
        Cv2.MeanStdDev(bgrFrame, out Scalar mean, out _);

        double meanB = Math.Max(mean.Val0, 1.0);
        double meanG = Math.Max(mean.Val1, 1.0);
        double meanR = Math.Max(mean.Val2, 1.0);
        double gray = (meanB + meanG + meanR) / 3.0;

        double gainB = Math.Clamp(gray / meanB, 0.5, 2.2);
        double gainG = Math.Clamp(gray / meanG, 0.5, 2.2);
        double gainR = Math.Clamp(gray / meanR, 0.5, 2.2);

        return (gainB, gainG, gainR);
    }

    /// <summary>Applies per-channel multiplicative gains (used for white balance).</summary>
    public static Mat ApplyChannelGains(Mat bgrFrame, double gainB, double gainG, double gainR)
    {
        Mat[] channels = bgrFrame.Split();
        try
        {
            channels[0].ConvertTo(channels[0], -1, gainB, 0);
            channels[1].ConvertTo(channels[1], -1, gainG, 0);
            channels[2].ConvertTo(channels[2], -1, gainR, 0);

            Mat merged = new();
            Cv2.Merge(channels, merged);
            return merged;
        }
        finally
        {
            foreach (Mat channel in channels)
            {
                channel.Dispose();
            }
        }
    }

    /// <summary>Measures mean luminance and its standard deviation inside a region (skin exposure probe).</summary>
    public static (double MeanLuminance, double StdDev) MeasureLuminance(Mat bgrFrame, Rect roi)
    {
        Rect clamped = ClampRect(roi, bgrFrame.Size());
        using Mat region = new(bgrFrame, clamped);
        using Mat gray = new();
        Cv2.CvtColor(region, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.MeanStdDev(gray, out Scalar mean, out Scalar stdDev);
        return (mean.Val0, stdDev.Val0);
    }

    /// <summary>
    /// Estimates high-frequency sensor noise energy inside a region using Laplacian
    /// variance - a higher value indicates a noisier/more detailed patch and calls for
    /// stronger bilateral smoothing.
    /// </summary>
    public static double EstimateNoiseLevel(Mat bgrFrame, Rect roi)
    {
        Rect clamped = ClampRect(roi, bgrFrame.Size());
        using Mat region = new(bgrFrame, clamped);
        using Mat gray = new();
        Cv2.CvtColor(region, gray, ColorConversionCodes.BGR2GRAY);
        using Mat laplacian = new();
        Cv2.Laplacian(gray, laplacian, MatType.CV_64F);
        Cv2.MeanStdDev(laplacian, out _, out Scalar stdDev);
        return stdDev.Val0;
    }

    /// <summary>
    /// Given a measured current luminance and a desired target luminance (both 0-255),
    /// solves for the gamma exponent g such that (current/255)^(1/g) == target/255.
    /// </summary>
    public static double SolveGammaForTargetLuminance(double currentLuminance, double targetLuminance)
    {
        double currentNorm = Math.Clamp(currentLuminance / 255.0, 0.02, 0.98);
        double targetNorm = Math.Clamp(targetLuminance / 255.0, 0.02, 0.98);

        double gamma = Math.Log(currentNorm) / Math.Log(targetNorm);
        return Math.Clamp(gamma, 0.4, 2.5);
    }

    /// <summary>Applies gamma correction via a precomputed 256-entry lookup table.</summary>
    public static Mat ApplyGamma(Mat src, double gamma)
    {
        if (Math.Abs(gamma - 1.0) < 0.005)
        {
            return src.Clone();
        }

        byte[] lutValues = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            double normalized = i / 255.0;
            double corrected = Math.Pow(normalized, 1.0 / gamma) * 255.0;
            lutValues[i] = (byte)Math.Clamp(corrected, 0, 255);
        }

        using Mat lut = new(1, 256, MatType.CV_8UC1);
        Marshal.Copy(lutValues, 0, lut.Data, 256);

        Mat dst = new();
        Cv2.LUT(src, lut, dst);
        return dst;
    }

    /// <summary>Applies linear contrast/brightness: output = input * alpha + beta.</summary>
    public static Mat ApplyContrastBrightness(Mat src, double alpha, double beta)
    {
        Mat dst = new();
        src.ConvertTo(dst, -1, alpha, beta);
        return dst;
    }

    /// <summary>Applies a bilateral filter (edge-preserving smoothing) when diameter &gt; 0.</summary>
    public static Mat ApplyBilateralFilter(Mat src, int diameter, double sigmaColor, double sigmaSpace)
    {
        if (diameter <= 0)
        {
            return src.Clone();
        }

        Mat dst = new();
        Cv2.BilateralFilter(src, dst, diameter, sigmaColor, sigmaSpace);
        return dst;
    }

    /// <summary>
    /// Runs the full correction pipeline (AWB -> gamma -> contrast/brightness -> bilateral)
    /// using a previously computed parameter set. Returns a brand-new Mat; the caller owns
    /// and must dispose it. When <paramref name="parameters"/> is the neutral default (no
    /// calibration has run yet) this simply returns a clone of the input.
    /// </summary>
    public Mat ProcessFrame(Mat rawBgrFrame, CalibrationParameters parameters)
    {
        Mat current = rawBgrFrame.Clone();

        if (!parameters.IsCalibrated)
        {
            return current;
        }

        Mat afterAwb = ApplyChannelGains(current, parameters.AwbGainB, parameters.AwbGainG, parameters.AwbGainR);
        current.Dispose();
        current = afterAwb;

        Mat afterGamma = ApplyGamma(current, parameters.Gamma);
        current.Dispose();
        current = afterGamma;

        Mat afterContrast = ApplyContrastBrightness(current, parameters.Alpha, parameters.Beta);
        current.Dispose();
        current = afterContrast;

        if (parameters.BilateralDiameter > 0)
        {
            Mat afterBilateral = ApplyBilateralFilter(current, parameters.BilateralDiameter, parameters.BilateralSigmaColor, parameters.BilateralSigmaSpace);
            current.Dispose();
            current = afterBilateral;
        }

        return current;
    }

    private static Rect ClampRect(Rect rect, Size bounds)
    {
        int x = Math.Clamp(rect.X, 0, Math.Max(0, bounds.Width - 1));
        int y = Math.Clamp(rect.Y, 0, Math.Max(0, bounds.Height - 1));
        int width = Math.Clamp(rect.Width, 1, bounds.Width - x);
        int height = Math.Clamp(rect.Height, 1, bounds.Height - y);
        return new Rect(x, y, width, height);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _faceCascade.Dispose();
        _disposed = true;
    }
}
