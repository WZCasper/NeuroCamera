using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace NeuroCamera.Engine;

/// <summary>
/// One-click installer for the UnityCapture DirectShow filter (the same free, open-source,
/// MIT-licensed driver <see cref="VirtualCamWriter"/> sends frames to - see
/// https://github.com/schellingb/UnityCapture). Downloads the official prebuilt 64-bit
/// filter DLL directly from that repository and registers it with the OS via an elevated
/// <c>regsvr32</c>, after which "Unity Video Capture" appears as a selectable camera in
/// OBS/Zoom/Teams/the Windows Camera app - equivalent to what the project's own Install.bat
/// does (<c>regsvr32 UnityCaptureFilter64.dll</c>), just triggered from inside NeuroCamera
/// instead of a separate downloaded script.
/// </summary>
public static class VirtualCameraInstaller
{
    private const string Filter64Url = "https://raw.githubusercontent.com/schellingb/UnityCapture/master/Install/UnityCaptureFilter64.dll";

    /// <summary>
    /// Downloads and registers the virtual camera driver. Triggers a native Windows UAC
    /// elevation prompt (registering a DirectShow filter requires admin rights); returns a
    /// human-readable outcome message either way instead of throwing for expected failures
    /// (network issues, the user declining the UAC prompt, regsvr32 rejecting the DLL).
    /// </summary>
    public static async Task<(bool Success, string Message)> InstallAsync()
    {
        string installDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "NeuroCamera", "UnityCapture");

        string dllPath = Path.Combine(installDir, "UnityCaptureFilter64.dll");

        try
        {
            Directory.CreateDirectory(installDir);

            using (var http = new HttpClient())
            {
                http.Timeout = TimeSpan.FromSeconds(30);
                byte[] dllBytes = await http.GetByteArrayAsync(Filter64Url).ConfigureAwait(false);
                await File.WriteAllBytesAsync(dllPath, dllBytes).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return (false, $"Не удалось скачать драйвер виртуальной камеры: {ex.Message}");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "regsvr32.exe",
            Arguments = $"/s \"{dllPath}\"",
            UseShellExecute = true,
            Verb = "runas"
        };

        try
        {
            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return (false, "Не удалось запустить regsvr32.");
            }

            await process.WaitForExitAsync().ConfigureAwait(false);

            return process.ExitCode == 0
                ? (true, "Готово: виртуальная камера \"Unity Video Capture\" установлена. Откройте её в Zoom/Teams/OBS.")
                : (false, $"regsvr32 завершился с ошибкой (код {process.ExitCode}).");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED - the user declined the UAC elevation prompt.
            return (false, "Установка отменена: требуется подтверждение прав администратора.");
        }
        catch (Exception ex)
        {
            return (false, $"Не удалось зарегистрировать драйвер: {ex.Message}");
        }
    }
}
