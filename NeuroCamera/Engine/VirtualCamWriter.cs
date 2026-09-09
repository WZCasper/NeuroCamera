using System.Runtime.InteropServices;

namespace NeuroCamera.Engine;

/// <summary>
/// Pushes processed RGBA32 frames into a Windows virtual camera device using the
/// "UnityCapture" shared-memory protocol (schellingb/UnityCapture, MIT/zlib licensed,
/// https://github.com/schellingb/UnityCapture). This class re-implements the sender
/// (producer) half of that protocol directly in managed code via P/Invoke - it does not
/// depend on Unity or on the UnityCapturePlugin native DLL.
///
/// IMPORTANT - this requires the free "UnityCapture" DirectShow filter to be installed once
/// on the machine (run Install.bat from the driver as Administrator). That filter is what
/// registers the actual "Unity Video Capture" device that Zoom/Teams/OBS/the Windows Camera
/// app can select as a webcam; no purely managed .NET application can register a new system
/// camera device by itself, because that requires a signed kernel/COM capture driver. This
/// class is only the client that feeds pixels into an already-installed driver instance.
///
/// Protocol summary (CapNum 0, the default/first capture device):
///  - A named mutex "UnityCapture_Mutx" guards the shared memory region.
///  - A named event "UnityCapture_Sent" is signalled by the sender after writing a frame.
///  - A named event "UnityCapture_Want" is signalled by the receiver when it wants a frame;
///    the sender creates this event (a fresh consumer may not have raised it yet).
///  - A named file mapping "UnityCapture_Data" holds a fixed-size header followed by the
///    raw pixel buffer.
///
/// All four objects are created by the *driver* the first time some application opens the
/// virtual camera device, so this class can only attach (Open) once such a consumer exists;
/// <see cref="TryConnect"/> is designed to be polled periodically and will succeed as soon
/// as the user opens the virtual camera in another application.
/// </summary>
public sealed class VirtualCamWriter : IDisposable
{
    private const uint SYNCHRONIZE = 0x00100000;
    private const uint EVENT_MODIFY_STATE = 0x0002;
    private const uint FILE_MAP_WRITE = 0x0002;
    private const uint WAIT_OBJECT_0 = 0x00000000;
    private const uint INFINITE = 0xFFFFFFFF;

    // SharedImageMemory::EFormat / EResizeMode / EMirrorMode from UnityCapture's shared.inl
    private const int FORMAT_UINT8 = 0;
    private const int RESIZEMODE_DISABLED = 0;
    private const int MIRRORMODE_DISABLED = 0;

    // Header layout (must match SharedMemHeader in shared.inl exactly, all 32-bit fields):
    // DWORD maxSize; int width; int height; int stride; int format; int resizemode; int mirrormode; int timeout; uint8_t data[1];
    private const int HeaderMaxSizeOffset = 0;
    private const int HeaderWidthOffset = 4;
    private const int HeaderHeightOffset = 8;
    private const int HeaderStrideOffset = 12;
    private const int HeaderFormatOffset = 16;
    private const int HeaderResizeModeOffset = 20;
    private const int HeaderMirrorModeOffset = 24;
    private const int HeaderTimeoutOffset = 28;
    private const int HeaderDataOffset = 32;

    private readonly int _capNum;
    private readonly object _connectLock = new();

    private IntPtr _mutex = IntPtr.Zero;
    private IntPtr _wantFrameEvent = IntPtr.Zero;
    private IntPtr _sentFrameEvent = IntPtr.Zero;
    private IntPtr _sharedFile = IntPtr.Zero;
    private IntPtr _mappedView = IntPtr.Zero;
    private uint _mappedMaxSize;

    private bool _disposed;

    /// <summary>Creates a writer targeting capture device slot 0 ("Unity Video Capture").</summary>
    public VirtualCamWriter() : this(capNum: 0)
    {
    }

    /// <summary>Creates a writer targeting a specific capture device slot (0-based).</summary>
    public VirtualCamWriter(int capNum)
    {
        if (capNum < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capNum));
        }

        _capNum = capNum;
    }

    /// <summary>True once the shared-memory handshake objects are successfully attached.</summary>
    public bool IsConnected { get; private set; }

    /// <summary>
    /// Attempts to attach to an already-running virtual camera receiver. Safe to call
    /// repeatedly (e.g. every couple of seconds from the video loop) until it succeeds -
    /// it will keep failing harmlessly until some application opens the virtual camera.
    /// </summary>
    public bool TryConnect()
    {
        lock (_connectLock)
        {
            if (IsConnected)
            {
                return true;
            }

            try
            {
                string suffix = _capNum == 0 ? string.Empty : ((char)('0' + _capNum)).ToString();

                if (_mutex == IntPtr.Zero)
                {
                    _mutex = OpenMutexA(SYNCHRONIZE, false, "UnityCapture_Mutx" + suffix);
                    if (_mutex == IntPtr.Zero)
                    {
                        return false;
                    }
                }

                if (_wantFrameEvent == IntPtr.Zero)
                {
                    _wantFrameEvent = CreateEventA(IntPtr.Zero, false, false, "UnityCapture_Want" + suffix);
                    if (_wantFrameEvent == IntPtr.Zero)
                    {
                        ReleasePartialHandles();
                        return false;
                    }
                }

                if (_sentFrameEvent == IntPtr.Zero)
                {
                    _sentFrameEvent = OpenEventA(EVENT_MODIFY_STATE, false, "UnityCapture_Sent" + suffix);
                    if (_sentFrameEvent == IntPtr.Zero)
                    {
                        ReleasePartialHandles();
                        return false;
                    }
                }

                if (_sharedFile == IntPtr.Zero)
                {
                    _sharedFile = OpenFileMappingA(FILE_MAP_WRITE, false, "UnityCapture_Data" + suffix);
                    if (_sharedFile == IntPtr.Zero)
                    {
                        ReleasePartialHandles();
                        return false;
                    }
                }

                if (_mappedView == IntPtr.Zero)
                {
                    _mappedView = MapViewOfFile(_sharedFile, FILE_MAP_WRITE, 0, 0, UIntPtr.Zero);
                    if (_mappedView == IntPtr.Zero)
                    {
                        ReleasePartialHandles();
                        return false;
                    }

                    _mappedMaxSize = (uint)Marshal.ReadInt32(_mappedView, HeaderMaxSizeOffset);
                }

                IsConnected = true;
                return true;
            }
            catch
            {
                ReleasePartialHandles();
                return false;
            }
        }
    }

    /// <summary>
    /// Sends one RGBA32 (4 bytes/pixel, top-down, tightly packed) frame to the virtual
    /// camera - this is the byte order UnityCapture's receiving filter expects for
    /// FORMAT_UINT8 (it converts RGBA -&gt; BGRA internally before handing frames to
    /// DirectShow consumers). Returns false (non-fatal) if the frame was rejected or the
    /// receiver disconnected mid-stream, in which case the caller should stop sending and
    /// periodically retry <see cref="TryConnect"/>.
    /// </summary>
    public bool SendFrame(byte[] rgbaPixels, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(rgbaPixels);

        if (!IsConnected && !TryConnect())
        {
            return false;
        }

        int stride = width * 4;
        uint dataSize = (uint)(stride * height);

        // maxSize (read from the header at connect time) is the driver's preallocated
        // *payload* capacity, not counting the 32-byte header - matches the reference
        // check `if (m_pSharedBuf->maxSize < DataSize) return SENDRES_TOOLARGE;`.
        if (dataSize > _mappedMaxSize)
        {
            return false;
        }

        try
        {
            uint waitResult = WaitForSingleObject(_mutex, INFINITE);
            if (waitResult != WAIT_OBJECT_0)
            {
                Disconnect();
                return false;
            }

            try
            {
                Marshal.WriteInt32(_mappedView, HeaderWidthOffset, width);
                Marshal.WriteInt32(_mappedView, HeaderHeightOffset, height);
                Marshal.WriteInt32(_mappedView, HeaderStrideOffset, stride);
                Marshal.WriteInt32(_mappedView, HeaderFormatOffset, FORMAT_UINT8);
                Marshal.WriteInt32(_mappedView, HeaderResizeModeOffset, RESIZEMODE_DISABLED);
                Marshal.WriteInt32(_mappedView, HeaderMirrorModeOffset, MIRRORMODE_DISABLED);
                Marshal.WriteInt32(_mappedView, HeaderTimeoutOffset, 1000);
                Marshal.Copy(rgbaPixels, 0, IntPtr.Add(_mappedView, HeaderDataOffset), (int)dataSize);
            }
            finally
            {
                ReleaseMutex(_mutex);
            }

            SetEvent(_sentFrameEvent);

            // If the "want frame" event is not currently signalled, the consumer did not
            // ask for a fresh frame since the last send - i.e. we are producing faster
            // than it is being read. Purely informational, mirrors the reference
            // implementation's SENDRES_WARN_FRAMESKIP; never treated as an error.
            LastFrameWasSkipped = WaitForSingleObject(_wantFrameEvent, 0) != WAIT_OBJECT_0;

            return true;
        }
        catch
        {
            Disconnect();
            return false;
        }
    }

    /// <summary>
    /// True if the most recent <see cref="SendFrame"/> call produced a frame the receiver
    /// had not yet asked for (we are producing faster than it consumes). Informational only.
    /// </summary>
    public bool LastFrameWasSkipped { get; private set; }

    /// <summary>Releases the shared-memory handshake so a future <see cref="TryConnect"/> starts clean.</summary>
    public void Disconnect()
    {
        lock (_connectLock)
        {
            ReleasePartialHandles();
            IsConnected = false;
        }
    }

    private void ReleasePartialHandles()
    {
        if (_mappedView != IntPtr.Zero)
        {
            UnmapViewOfFile(_mappedView);
            _mappedView = IntPtr.Zero;
        }

        if (_sharedFile != IntPtr.Zero)
        {
            CloseHandle(_sharedFile);
            _sharedFile = IntPtr.Zero;
        }

        if (_sentFrameEvent != IntPtr.Zero)
        {
            CloseHandle(_sentFrameEvent);
            _sentFrameEvent = IntPtr.Zero;
        }

        if (_wantFrameEvent != IntPtr.Zero)
        {
            CloseHandle(_wantFrameEvent);
            _wantFrameEvent = IntPtr.Zero;
        }

        if (_mutex != IntPtr.Zero)
        {
            CloseHandle(_mutex);
            _mutex = IntPtr.Zero;
        }

        _mappedMaxSize = 0;
        IsConnected = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Disconnect();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    ~VirtualCamWriter()
    {
        ReleasePartialHandles();
    }

    #region Win32 P/Invoke

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr OpenMutexA(uint dwDesiredAccess, bool bInheritHandle, string lpName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr CreateEventA(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string lpName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr OpenEventA(uint dwDesiredAccess, bool bInheritHandle, string lpName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr OpenFileMappingA(uint dwDesiredAccess, bool bInheritHandle, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(IntPtr hFileMappingObject, uint dwDesiredAccess, uint dwFileOffsetHigh, uint dwFileOffsetLow, UIntPtr dwNumberOfBytesToMap);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetEvent(IntPtr hEvent);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReleaseMutex(IntPtr hMutex);

    #endregion
}
