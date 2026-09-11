using System.Runtime.InteropServices;

namespace NeuroCamera.Interop;

/// <summary>
/// Video quality properties exposed through <see cref="IAMVideoProcAmp"/> (brightness,
/// contrast, etc.). Integer values match the Win32 <c>VideoProcAmpProperty</c> enumeration
/// exactly (Microsoft Learn: "VideoProcAmpProperty Enumeration") - do not reorder.
/// </summary>
public enum VideoProcAmpProperty
{
    Brightness = 0,
    Contrast = 1,
    Hue = 2,
    Saturation = 3,
    Sharpness = 4,
    Gamma = 5,
    ColorEnable = 6,
    WhiteBalance = 7,
    BacklightCompensation = 8,
    Gain = 9
}

/// <summary>
/// Camera properties exposed through <see cref="IAMCameraControl"/> (exposure, focus, etc.).
/// Integer values match the Win32 <c>CameraControlProperty</c> enumeration exactly - do not
/// reorder.
/// </summary>
public enum CameraControlProperty
{
    Pan = 0,
    Tilt = 1,
    Roll = 2,
    Zoom = 3,
    Exposure = 4,
    Iris = 5,
    Focus = 6
}

/// <summary>Control-setting flags shared by both interfaces (Win32 VideoProcAmpFlags / CameraControlFlags - identical values).</summary>
[Flags]
public enum CameraPropertyFlags
{
    None = 0,
    Auto = 0x0001,
    Manual = 0x0002
}

/// <summary>
/// Adjusts image-signal qualities (brightness, contrast, hue, saturation, sharpness, gamma,
/// white balance, backlight compensation, gain) on a DirectShow video capture filter.
/// IID and method shapes verified against Microsoft's official documentation
/// (learn.microsoft.com/.../nn-strmif-iamvideoprocamp) and the widely used open-source
/// AForge.NET / DirectShowLib interop declarations.
/// </summary>
[ComImport]
[Guid("C6E13360-30AC-11d0-A18C-00A0C9118956")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAMVideoProcAmp
{
    [PreserveSig]
    int GetRange(VideoProcAmpProperty property, out int min, out int max, out int steppingDelta, out int defaultValue, out CameraPropertyFlags capsFlags);

    [PreserveSig]
    int Set(VideoProcAmpProperty property, int value, CameraPropertyFlags flags);

    [PreserveSig]
    int Get(VideoProcAmpProperty property, out int value, out CameraPropertyFlags flags);
}

/// <summary>
/// Controls camera-body settings (pan, tilt, roll, zoom, exposure, iris, focus) on a
/// DirectShow video capture filter. IID and method shapes verified the same way as
/// <see cref="IAMVideoProcAmp"/> above.
/// </summary>
[ComImport]
[Guid("C6E13370-30AC-11d0-A18C-00A0C9118956")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAMCameraControl
{
    [PreserveSig]
    int GetRange(CameraControlProperty property, out int min, out int max, out int steppingDelta, out int defaultValue, out CameraPropertyFlags capsFlags);

    [PreserveSig]
    int Set(CameraControlProperty property, int value, CameraPropertyFlags flags);

    [PreserveSig]
    int Get(CameraControlProperty property, out int value, out CameraPropertyFlags flags);
}
