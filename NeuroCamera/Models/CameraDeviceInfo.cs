namespace NeuroCamera.Models;

/// <summary>
/// Represents a physical video capture device as enumerated from the Windows
/// DirectShow "Video Input Device" category. <see cref="Index"/> corresponds to the
/// device ordinal expected by <c>OpenCvSharp.VideoCapture</c> when opened with
/// <c>VideoCaptureAPIs.DSHOW</c>, since both APIs walk the same system device enumerator.
/// </summary>
public sealed class CameraDeviceInfo
{
    public int Index { get; }
    public string Name { get; }

    public CameraDeviceInfo(int index, string name)
    {
        Index = index;
        Name = name;
    }

    public override string ToString() => Name;

    public override bool Equals(object? obj) =>
        obj is CameraDeviceInfo other && other.Index == Index;

    public override int GetHashCode() => Index.GetHashCode();
}
