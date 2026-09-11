using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using NeuroCamera.Interop;
using NeuroCamera.Models;

namespace NeuroCamera.Engine;

/// <summary>
/// Reads and writes a physical camera's own hardware/driver-level properties (brightness,
/// contrast, exposure, white balance temperature, gain, etc.) via the same DirectShow
/// interfaces (IAMVideoProcAmp / IAMCameraControl) that OBS's own "Configure Video"
/// properties dialog uses - so the numbers this class reports are the exact same numbers
/// OBS would show for the same device, letting the person read a value here and dial in the
/// identical value there.
///
/// This is independent of, and unrelated to, NeuroCamera's own software image correction
/// (<see cref="ImageProcessor"/>/<see cref="CalibrationEngine"/>) - it controls the camera
/// itself, not pixels after capture.
///
/// Instances are only safe to use from the thread that created them (COM/STA affinity,
/// matching WPF's UI thread) - use from the UI thread only, same as
/// <see cref="NeuroCamera.Interop.DirectShowInterop"/>.
/// </summary>
public sealed class HardwareCameraController : IDisposable
{
    private static readonly Guid IidBaseFilter = new("56a86895-0ad4-11ce-b03a-0020af0ba770");

    private readonly object _filterObject;
    private readonly IAMVideoProcAmp? _videoProcAmp;
    private readonly IAMCameraControl? _cameraControl;
    private bool _disposed;

    private HardwareCameraController(object filterObject, IAMVideoProcAmp? videoProcAmp, IAMCameraControl? cameraControl)
    {
        _filterObject = filterObject;
        _videoProcAmp = videoProcAmp;
        _cameraControl = cameraControl;
    }

    /// <summary>
    /// Attempts to open the hardware control interfaces for the given device index (same
    /// index as <see cref="NeuroCamera.Interop.DirectShowInterop.EnumerateVideoInputDeviceNames"/>).
    /// Returns null if the device can't be bound or exposes neither control interface.
    /// </summary>
    public static HardwareCameraController? TryOpen(int deviceIndex)
    {
        IMoniker? moniker = DirectShowInterop.GetMonikerForDeviceIndex(deviceIndex);
        if (moniker is null)
        {
            return null;
        }

        try
        {
            Guid iid = IidBaseFilter;
            moniker.BindToObject(null!, null!, ref iid, out object filterObject);

            var videoProcAmp = filterObject as IAMVideoProcAmp;
            var cameraControl = filterObject as IAMCameraControl;

            if (videoProcAmp is null && cameraControl is null)
            {
                Marshal.ReleaseComObject(filterObject);
                return null;
            }

            return new HardwareCameraController(filterObject, videoProcAmp, cameraControl);
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            Marshal.ReleaseComObject(moniker);
        }
    }

    /// <summary>
    /// Reads every property the connected driver reports supporting (GetRange succeeding is
    /// the driver's way of saying "yes, I support this"). Unsupported properties are simply
    /// omitted, so the UI only ever shows sliders that will actually do something.
    /// </summary>
    public IReadOnlyList<CameraControlSetting> EnumerateSettings()
    {
        var results = new List<CameraControlSetting>();

        if (_videoProcAmp is not null)
        {
            foreach (VideoProcAmpProperty property in Enum.GetValues<VideoProcAmpProperty>())
            {
                if (TryDescribeVideoProcAmp(property, out CameraControlSetting? setting))
                {
                    results.Add(setting!);
                }
            }
        }

        if (_cameraControl is not null)
        {
            foreach (CameraControlProperty property in Enum.GetValues<CameraControlProperty>())
            {
                if (TryDescribeCameraControl(property, out CameraControlSetting? setting))
                {
                    results.Add(setting!);
                }
            }
        }

        return results;
    }

    /// <summary>Applies a new value (and manual/auto mode) to the camera and returns whether the driver accepted it.</summary>
    public bool Set(CameraControlSetting setting, int value, bool auto)
    {
        CameraPropertyFlags flags = auto ? CameraPropertyFlags.Auto : CameraPropertyFlags.Manual;

        int hr = setting.Kind == CameraControlKind.VideoProcAmp
            ? _videoProcAmp?.Set((VideoProcAmpProperty)setting.PropertyIndex, value, flags) ?? -1
            : _cameraControl?.Set((CameraControlProperty)setting.PropertyIndex, value, flags) ?? -1;

        return hr == 0;
    }

    private bool TryDescribeVideoProcAmp(VideoProcAmpProperty property, out CameraControlSetting? setting)
    {
        setting = null;
        if (_videoProcAmp is null)
        {
            return false;
        }

        if (_videoProcAmp.GetRange(property, out int min, out int max, out int step, out int def, out CameraPropertyFlags caps) != 0 || min >= max)
        {
            return false;
        }

        bool supportsAuto = caps.HasFlag(CameraPropertyFlags.Auto);
        int current = def;
        bool currentIsAuto = supportsAuto;
        if (_videoProcAmp.Get(property, out int gotValue, out CameraPropertyFlags gotFlags) == 0)
        {
            current = gotValue;
            currentIsAuto = gotFlags.HasFlag(CameraPropertyFlags.Auto);
        }

        setting = new CameraControlSetting(
            CameraControlKind.VideoProcAmp, (int)property, DisplayNameFor(property),
            min, max, Math.Max(step, 1), def, supportsAuto, current, currentIsAuto);
        return true;
    }

    private bool TryDescribeCameraControl(CameraControlProperty property, out CameraControlSetting? setting)
    {
        setting = null;
        if (_cameraControl is null)
        {
            return false;
        }

        if (_cameraControl.GetRange(property, out int min, out int max, out int step, out int def, out CameraPropertyFlags caps) != 0 || min >= max)
        {
            return false;
        }

        bool supportsAuto = caps.HasFlag(CameraPropertyFlags.Auto);
        int current = def;
        bool currentIsAuto = supportsAuto;
        if (_cameraControl.Get(property, out int gotValue, out CameraPropertyFlags gotFlags) == 0)
        {
            current = gotValue;
            currentIsAuto = gotFlags.HasFlag(CameraPropertyFlags.Auto);
        }

        setting = new CameraControlSetting(
            CameraControlKind.CameraControl, (int)property, DisplayNameFor(property),
            min, max, Math.Max(step, 1), def, supportsAuto, current, currentIsAuto);
        return true;
    }

    private static string DisplayNameFor(VideoProcAmpProperty property) => property switch
    {
        VideoProcAmpProperty.Brightness => "Яркость",
        VideoProcAmpProperty.Contrast => "Контраст",
        VideoProcAmpProperty.Hue => "Оттенок",
        VideoProcAmpProperty.Saturation => "Насыщенность",
        VideoProcAmpProperty.Sharpness => "Резкость",
        VideoProcAmpProperty.Gamma => "Гамма",
        VideoProcAmpProperty.ColorEnable => "Цвет включён",
        VideoProcAmpProperty.WhiteBalance => "Баланс белого, K",
        VideoProcAmpProperty.BacklightCompensation => "Компенсация подсветки",
        VideoProcAmpProperty.Gain => "Усиление (Gain)",
        _ => property.ToString()
    };

    private static string DisplayNameFor(CameraControlProperty property) => property switch
    {
        CameraControlProperty.Pan => "Панорама",
        CameraControlProperty.Tilt => "Наклон",
        CameraControlProperty.Roll => "Поворот",
        CameraControlProperty.Zoom => "Зум",
        CameraControlProperty.Exposure => "Экспозиция",
        CameraControlProperty.Iris => "Диафрагма",
        CameraControlProperty.Focus => "Фокус",
        _ => property.ToString()
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Marshal.ReleaseComObject(_filterObject);
        _disposed = true;
    }
}
