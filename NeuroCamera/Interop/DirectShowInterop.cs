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

    /// <summary>
    /// Enumerates the friendly names of every video capture device currently registered
    /// with the OS, in the same enumeration order DirectShow (and therefore OpenCvSharp's
    /// DSHOW backend) will assign as device indices 0, 1, 2, ...
    /// </summary>
    public static List<string> EnumerateVideoInputDeviceNames()
    {
        var names = new List<string>();

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
                return names;
            }

            var monikers = new IMoniker[1];
            while (monikerEnum.Next(1, monikers, IntPtr.Zero) == 0)
            {
                IMoniker moniker = monikers[0];
                try
                {
                    names.Add(TryReadFriendlyName(moniker, fallback: $"Камера {names.Count}"));
                }
                finally
                {
                    Marshal.ReleaseComObject(moniker);
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

        return names;
    }

    /// <summary>
    /// Returns the raw <see cref="IMoniker"/> for the Nth video input device (same order as
    /// <see cref="EnumerateVideoInputDeviceNames"/> and the same order OpenCvSharp's DSHOW
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
