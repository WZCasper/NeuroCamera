using NeuroCamera.Common;
using Xunit;

namespace NeuroCamera.Tests;

public class CrashLogTests
{
    private static string NewTempFile()
    {
        string directory = Path.Combine(Path.GetTempPath(), "neurocamera-tests-" + Guid.NewGuid().ToString("N"));
        return Path.Combine(directory, "crash.log");
    }

    [Fact]
    public void An_entry_contains_the_context_the_exception_type_and_message()
    {
        string path = NewTempFile();
        try
        {
            CrashLog.Write("UI thread", new InvalidOperationException("something broke"), path);

            string text = File.ReadAllText(path);
            Assert.Contains("UI thread", text);
            Assert.Contains("InvalidOperationException", text);
            Assert.Contains("something broke", text);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void Entries_are_appended()
    {
        string path = NewTempFile();
        try
        {
            CrashLog.Write("first", new Exception("one"), path);
            CrashLog.Write("second", new Exception("two"), path);

            string text = File.ReadAllText(path);
            Assert.Contains("one", text);
            Assert.Contains("two", text);
            Assert.True(text.IndexOf("one", StringComparison.Ordinal) < text.IndexOf("two", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void The_file_is_restarted_once_it_grows_past_the_size_cap()
    {
        string path = NewTempFile();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, new string('x', 600 * 1024));

            CrashLog.Write("after overflow", new Exception("fresh"), path);

            Assert.True(new FileInfo(path).Length < 10 * 1024);
            Assert.Contains("fresh", File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void An_unwritable_path_never_throws()
    {
        string directory = Path.Combine(Path.GetTempPath(), "neurocamera-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string blocker = Path.Combine(directory, "i-am-a-file");
            File.WriteAllText(blocker, "x");

            CrashLog.Write("context", new Exception("ignored"), Path.Combine(blocker, "crash.log"));

            Assert.True(File.Exists(blocker));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
