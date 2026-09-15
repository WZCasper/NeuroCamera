using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;

namespace NeuroCamera.Common;

/// <summary>
/// Converts an OpenCvSharp <see cref="Mat"/> into a frozen (thread-safe, immutable) WPF
/// <see cref="BitmapSource"/> for the live preview. Does not dispose or mutate the Mat passed in.
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
}

