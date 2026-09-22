using System.IO;
using System.Text;

namespace NeuroCamera.Common;

/// <summary>
/// Appends unhandled-exception details to <c>%AppData%\NeuroCamera\crash.log</c> so that
/// "it stopped working" reports come with a stack trace instead of only a message box.
/// Strictly best-effort: logging must never be able to throw or make a crash worse.
/// </summary>
public static class CrashLog
{
    private const long MaxFileBytes = 512 * 1024;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NeuroCamera", "crash.log");

    public static void Write(string context, Exception exception) => Write(context, exception, DefaultPath);

    internal static void Write(string context, Exception exception, string path)
    {
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Keep the file bounded: once it grows past the cap, start over.
            FileInfo info = new(path);
            if (info.Exists && info.Length > MaxFileBytes)
            {
                info.Delete();
            }

            StringBuilder entry = new();
            entry.Append('[').Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz")).Append("] ").AppendLine(context);
            entry.AppendLine(exception.ToString());
            entry.AppendLine();

            File.AppendAllText(path, entry.ToString(), Encoding.UTF8);
        }
        catch
        {
            // Nothing sensible left to do if even the log cannot be written.
        }
    }
}
