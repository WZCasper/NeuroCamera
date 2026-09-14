using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace NeuroCamera.Interop;

/// <summary>
/// Minimal DirectShow COM interop surface needed to enumerate the friendly names of the
/// system's video capture devices (the same set OpenCvSharp's DSHOW backend indexes by
/// ordinal). Only the members required for enumeration are declared.
/// </summary>
internal static class DirectShowInterop
{
    // CLSID_SystemDeviceEnum
    [ComImport]
    [Guid("62BE5D10-60EB-11d0-BD3B-00A0C911CE86")]
    private class SystemDeviceEnum
    {
    }

    // CLSID_VideoInputDeviceCategory
    private static readonly Guid CLSID_VideoInputDeviceCategory = new("860BB310-5D01-11d0-BD3B-00A0C911CE86");

    [ComImport]
    [Guid("29840822-5B84-11D0-BD3B-00A0C911CE86")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICreateDevEnum
    {
        [PreserveSig]
        int CreateClassEnumerator(ref Guid pType, out IEnumMoniker? ppEnumMoniker, int dwFlags);
    }

    [ComImport]
    [Guid("55272A00-42CB-11CE-8135-00AA004BB851")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyBag
    {
        [PreserveSig]
        int Read(
            [MarshalAs(UnmanagedType.LPWStr)] string propertyName,
            [MarshalAs(UnmanagedType.Struct)] out object value,
            IntPtr errorLog);

        [PreserveSig]
        int Write(
            [MarshalAs(UnmanagedType.LPWStr)] string propertyName,
            [MarshalAs(UnmanagedType.Struct)] ref object value);
    }

    /// <summary>One physical video input device as DirectShow enumerates it.</summary>
    public sealed record VideoInputDevice(int Index, string Name);

    /// <summary>
    /// Enumerates real, physical video capture devices only - virtual/software-registered
    /// ones (Unity Video Capture, OBS Virtual Camera, Snap Camera, and any other DirectShow
    /// filter that was regsvr32'd rather than backed by actual PnP hardware) are excluded, so
    /// NeuroCamera's own input picker never lists its own virtual output or similar noise.
    ///
    /// This is not a guessed name denylist: DirectShow itself encodes the distinction in each
    /// moniker's display name - real hardware devices have a display name starting with
    /// "@device:pnp:", while filters registered purely via COM (regsvr32, no PnP hardware
    /// behind them) start with "@device:sw:{CLSID}". Filtering on that prefix is the same
    /// signal DirectShow-aware software uses generally, not specific to any one virtual
    /// camera product.
    ///
    /// <see cref="VideoInputDevice.Index"/> is the device's true position in DirectShow's own
    /// enumeration order (i.e. what OpenCvSharp's DSHOW backend expects as a device index) -
    /// it is preserved even though filtered-out devices leave gaps, since VideoCapture must be
    /// opened with the *real* index, not a position within this filtered list.
    /// </summary>
    public static List<VideoInputDevice> EnumerateVideoInputDevices()
    {
        var devices = new List<VideoInputDevice>();

        object? comEnumInstance = null;
        IEnumMoniker? monikerEnum = null;

        try
        {
            comEnumInstance = new SystemDeviceEnum();
            var createDevEnum = (ICreateDevEnum)comEnumInstance;

            Guid category = CLSID_VideoInputDeviceCategory;
            int hr = createDevEnum.CreateClassEnumerator(ref category, out monikerEnum, 0);

            // S_FALSE (1) means the category exists but is empty (no cameras attached).
            if (hr != 0 || monikerEnum is null)
            {
                return devices;
            }

            var monikers = new IMoniker[1];
            int rawIndex = 0;
            while (monikerEnum.Next(1, monikers, IntPtr.Zero) == 0)
            {
                IMoniker moniker = monikers[0];
                try
                {
                    if (IsPhysicalHardwareDevice(moniker))
                    {
                        string name = TryReadFriendlyName(moniker, fallback: $"Камера {rawIndex}");
                        devices.Add(new VideoInputDevice(rawIndex, name));
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(moniker);
                    rawIndex++;
                }
            }
        }
        catch (COMException)
        {
            // No DirectShow device enumerator available (e.g. running outside Windows
            // desktop context). Return whatever was collected so far - possibly empty.
        }
        finally
        {
            if (monikerEnum is not null)
            {
                Marshal.ReleaseComObject(monikerEnum);
            }

            if (comEnumInstance is not null)
            {
                Marshal.ReleaseComObject(comEnumInstance);
            }
        }

        return devices;
    }

    /// <summary>True if any registered video input device's friendly name matches (used to check whether the virtual camera driver is already installed).</summary>
    public static bool AnyDeviceNameContains(string substring)
    {
        foreach (VideoInputDevice device in EnumerateVideoInputDevices_IncludingVirtual())
        {
            if (device.Name.Contains(substring, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Same as <see cref="EnumerateVideoInputDevices"/> but without the physical-hardware filter - used only for install-state checks like <see cref="AnyDeviceNameContains"/>.</summary>
    private static List<VideoInputDevice> EnumerateVideoInputDevices_IncludingVirtual()
    {
        var devices = new List<VideoInputDevice>();
        object? comEnumInstance = null;
        IEnumMoniker? monikerEnum = null;

        try
        {
            comEnumInstance = new SystemDeviceEnum();
            var createDevEnum = (ICreateDevEnum)comEnumInstance;

            Guid category = CLSID_VideoInputDeviceCategory;
            int hr = createDevEnum.CreateClassEnumerator(ref category, out monikerEnum, 0);
            if (hr != 0 || monikerEnum is null)
            {
                return devices;
            }

            var monikers = new IMoniker[1];
            int rawIndex = 0;
            while (monikerEnum.Next(1, monikers, IntPtr.Zero) == 0)
            {
                IMoniker moniker = monikers[0];
                try
                {
                    devices.Add(new VideoInputDevice(rawIndex, TryReadFriendlyName(moniker, fallback: $"Камера {rawIndex}")));
                }
                finally
                {
                    Marshal.ReleaseComObject(moniker);
                    rawIndex++;
                }
            }
        }
        catch (COMException)
        {
        }
        finally
        {
            if (monikerEnum is not null)
            {
                Marshal.ReleaseComObject(monikerEnum);
            }

            if (comEnumInstance is not null)
            {
                Marshal.ReleaseComObject(comEnumInstance);
            }
        }

        return devices;
    }

    private static bool IsPhysicalHardwareDevice(IMoniker moniker)
    {
        try
        {
            moniker.GetDisplayName(null!, null!, out string displayName);
            // Real PnP hardware: "@device:pnp:\\?\usb#vid_...". Software-registered filters
            // (virtual cameras, including our own Unity Video Capture output): "@device:sw:{...}".
            return displayName.StartsWith("@device:pnp:", StringComparison.OrdinalIgnoreCase);
        }
        catch (COMException)
        {
            // If we can't determine the kind, default to showing it rather than hiding a
            // possibly-real device.
            return true;
        }
    }

    /// <summary>
    /// Returns the raw <see cref="IMoniker"/> for the Nth video input device (same order as
    /// <see cref="EnumerateVideoInputDevices"/> and the same order OpenCvSharp's DSHOW
    /// backend assigns device indices). The caller owns the returned moniker and must
    /// release it with <see cref="Marshal.ReleaseComObject"/> when done. Returns null if the
    /// index is out of range or no device enumerator is available.
    /// </summary>
    public static IMoniker? GetMonikerForDeviceIndex(int deviceIndex)
    {
        object? comEnumInstance = null;
        IEnumMoniker? monikerEnum = null;

        try
        {
            comEnumInstance = new SystemDeviceEnum();
            var createDevEnum = (ICreateDevEnum)comEnumInstance;

            Guid category = CLSID_VideoInputDeviceCategory;
            int hr = createDevEnum.CreateClassEnumerator(ref category, out monikerEnum, 0);
            if (hr != 0 || monikerEnum is null)
            {
                return null;
            }

            var monikers = new IMoniker[1];
            int index = 0;
            while (monikerEnum.Next(1, monikers, IntPtr.Zero) == 0)
            {
                if (index == deviceIndex)
                {
                    return monikers[0]; // ownership transferred to the caller
                }

                Marshal.ReleaseComObject(monikers[0]);
                index++;
            }

            return null;
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            if (monikerEnum is not null)
            {
                Marshal.ReleaseComObject(monikerEnum);
            }

            if (comEnumInstance is not null)
            {
                Marshal.ReleaseComObject(comEnumInstance);
            }
        }
    }

    private static string TryReadFriendlyName(IMoniker moniker, string fallback)
    {
        try
        {
            Guid propertyBagGuid = typeof(IPropertyBag).GUID;
            moniker.BindToStorage(null!, null!, ref propertyBagGuid, out object bagObj);
            if (bagObj is IPropertyBag bag)
            {
                int hr = bag.Read("FriendlyName", out object value, IntPtr.Zero);
                if (hr == 0 && value is string name && !string.IsNullOrWhiteSpace(name))
                {
                    return name;
                }
            }
        }
        catch (COMException)
        {
            // Some virtual/system monikers do not expose FriendlyName - fall back below.
        }

        return fallback;
    }
}
