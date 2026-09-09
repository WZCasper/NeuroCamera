using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;

namespace NeuroCamera.Common;

/// <summary>
/// Pixel-buffer conversions between OpenCvSharp <see cref="Mat"/> and the two formats the
/// rest of the app needs: an immutable, cross-thread-safe WPF <see cref="BitmapSource"/> for
/// the live preview, and a tightly packed BGRA byte array for <see cref="NeuroCamera.Engine.VirtualCamWriter"/>.
/// Neither method disposes or mutates the Mat passed in.
/// </summary>
public static class MatImageConverter
{
    /// <summary>
    /// Converts a BGR/BGRA/grayscale Mat into a frozen (thread-safe, immutable)
    /// <see cref="BitmapSource"/> that can be safely handed from the video thread to the UI
    /// thread via the Dispatcher without any further synchronization.
    /// </summary>
    public static BitmapSource ToBitmapSource(Mat mat)
    {
        Mat working = mat.IsContinuous() ? mat : mat.Clone();
        try
        {
            int width = working.Width;
            int height = working.Height;
            int channels = working.Channels();
            int stride = (int)working.Step();
            int length = stride * height;

            byte[] buffer = new byte[length];
            Marshal.Copy(working.Data, buffer, 0, length);

            PixelFormat format = channels switch
            {
                1 => PixelFormats.Gray8,
                3 => PixelFormats.Bgr24,
                4 => PixelFormats.Bgra32,
                _ => throw new NotSupportedException($"Unsupported channel count: {channels}")
            };

            BitmapSource bitmap = BitmapSource.Create(width, height, 96.0, 96.0, format, null, buffer, stride);
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            if (!ReferenceEquals(working, mat))
            {
                working.Dispose();
            }
        }
    }

    /// <summary>
    /// Converts a 3-channel BGR Mat into a tightly packed RGBA byte array
    /// (top-down, stride = width * 4) - the exact pixel layout UnityCapture's FORMAT_UINT8
    /// shared-memory protocol expects (its receiving filter converts RGBA -&gt; BGRA
    /// internally), consumed by <see cref="NeuroCamera.Engine.VirtualCamWriter"/>.
    /// </summary>
    public static byte[] ToRgbaBytes(Mat bgrMat, out int width, out int height)
    {
        Mat rgba = new();
        Cv2.CvtColor(bgrMat, rgba, ColorConversionCodes.BGR2RGBA);

        Mat continuous = rgba.IsContinuous() ? rgba : rgba.Clone();
        try
        {
            width = continuous.Width;
            height = continuous.Height;
            int stride = (int)continuous.Step();
            int expectedStride = width * 4;

            byte[] buffer = new byte[expectedStride * height];
            if (stride == expectedStride)
            {
                Marshal.Copy(continuous.Data, buffer, 0, buffer.Length);
            }
            else
            {
                // Defensive path in case of row padding; RGBA is normally unpadded.
                for (int row = 0; row < height; row++)
                {
                    IntPtr rowPtr = IntPtr.Add(continuous.Data, row * stride);
                    Marshal.Copy(rowPtr, buffer, row * expectedStride, expectedStride);
                }
            }

            return buffer;
        }
        finally
        {
            if (!ReferenceEquals(continuous, rgba))
            {
                continuous.Dispose();
            }

            rgba.Dispose();
        }
    }
}
