using NeuroCamera.Engine;
using NeuroCamera.Models;
using Xunit;

namespace NeuroCamera.Tests;

public class SettingsStoreTests
{
    private static string NewTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "neurocamera-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void Saved_settings_are_loaded_back_unchanged()
    {
        string directory = NewTempDirectory();
        try
        {
            string path = Path.Combine(directory, "settings.json");
            AppSettings original = new()
            {
                SelectedCameraName = "Logitech BRIO",
                ResolutionWidth = 1920,
                ResolutionHeight = 1080,
                ObsHost = "192.168.1.20",
                ObsPort = 4466,
                ObsSourceName = "Camera",
                ObsFilterName = "MyFilter",
                LastCalibration = new CalibrationParameters
                {
                    AwbGainB = 1.05,
                    AwbGainG = 1.0,
                    AwbGainR = 0.95,
                    Gamma = 1.2,
                    Alpha = 1.1,
                    Beta = -6,
                    BilateralDiameter = 7,
                    BilateralSigmaColor = 40,
                    BilateralSigmaSpace = 40,
                    IsCalibrated = true,
                    SceneWasDark = true
                }
            };

            SettingsStore.Save(original, path);
            AppSettings loaded = SettingsStore.Load(path);

            Assert.Equal("Logitech BRIO", loaded.SelectedCameraName);
            Assert.Equal(1920, loaded.ResolutionWidth);
            Assert.Equal(1080, loaded.ResolutionHeight);
            Assert.Equal("192.168.1.20", loaded.ObsHost);
            Assert.Equal(4466, loaded.ObsPort);
            Assert.Equal("Camera", loaded.ObsSourceName);
            Assert.Equal("MyFilter", loaded.ObsFilterName);
            Assert.NotNull(loaded.LastCalibration);
            Assert.Equal(original.LastCalibration, loaded.LastCalibration);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_missing_file_yields_defaults()
    {
        string directory = NewTempDirectory();
        try
        {
            AppSettings loaded = SettingsStore.Load(Path.Combine(directory, "does-not-exist.json"));

            Assert.Equal("localhost", loaded.ObsHost);
            Assert.Equal(4455, loaded.ObsPort);
            Assert.Null(loaded.LastCalibration);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_corrupt_file_yields_defaults_instead_of_throwing()
    {
        string directory = NewTempDirectory();
        try
        {
            string path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, "{ \"ObsHost\": \"broken\", \"ObsPort\": ");

            AppSettings loaded = SettingsStore.Load(path);

            Assert.Equal("localhost", loaded.ObsHost);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Saving_replaces_the_previous_file_and_leaves_no_temporary_file_behind()
    {
        string directory = NewTempDirectory();
        try
        {
            string path = Path.Combine(directory, "settings.json");

            SettingsStore.Save(new AppSettings { ObsHost = "first" }, path);
            SettingsStore.Save(new AppSettings { ObsHost = "second" }, path);

            Assert.Equal("second", SettingsStore.Load(path).ObsHost);
            Assert.Equal(new[] { "settings.json" }, Directory.GetFiles(directory).Select(Path.GetFileName).ToArray());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Saving_creates_missing_parent_directories()
    {
        string directory = NewTempDirectory();
        try
        {
            string path = Path.Combine(directory, "nested", "deeper", "settings.json");

            SettingsStore.Save(new AppSettings { ObsHost = "nested" }, path);

            Assert.Equal("nested", SettingsStore.Load(path).ObsHost);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Saving_to_an_impossible_location_does_not_throw()
    {
        string directory = NewTempDirectory();
        try
        {
            string blocker = Path.Combine(directory, "i-am-a-file");
            File.WriteAllText(blocker, "x");

            SettingsStore.Save(new AppSettings(), Path.Combine(blocker, "settings.json"));

            Assert.True(File.Exists(blocker));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
