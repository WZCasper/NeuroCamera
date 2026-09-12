using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using NeuroCamera.Common;
using NeuroCamera.Engine;
using NeuroCamera.Interop;
using NeuroCamera.Models;

namespace NeuroCamera.ViewModels;

/// <summary>
/// UI-facing state and commands for <c>MainWindow</c>. Owns the <see cref="VideoEngine"/>
/// (software correction + preview + virtual-camera output) and, independently, a
/// <see cref="HardwareCameraController"/> for the selected device's own driver-level
/// properties (brightness/exposure/etc., exposed as sliders). Every background-thread event
/// from <see cref="VideoEngine"/> is marshalled onto the WPF Dispatcher before touching any
/// bindable property, so the view model itself is safe to bind directly from XAML.
///
/// Camera choice, resolution, and the last completed calibration are persisted via
/// <see cref="SettingsStore"/> so the app comes back up already configured next time.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly VideoEngine _videoEngine;
    private readonly System.Windows.Threading.Dispatcher _dispatcher;
    private readonly AppSettings _settings;

    private HardwareCameraController? _hardwareController;

    private CameraDeviceInfo? _selectedCamera;
    private CaptureResolution _selectedResolution;
    private BitmapSource? _previewFrame;
    private bool _isStreaming;
    private bool _isCalibrating;
    private double _calibrationProgress;
    private string _calibrationStatusText = "Нажмите «Автонастройка», чтобы откалибровать камеру за 15 секунд.";
    private string _virtualCameraStatusText = "Виртуальная камера не подключена.";
    private string _virtualCameraInstallStatusText = "";
    private bool _isInstallingVirtualCamera;
    private string _resolutionStatusText = "";
    private double _manualBrightnessOffset;
    private bool _darkSceneHintVisible;
    private string? _errorText;
    private bool _disposed;

    public MainViewModel()
    {
        _dispatcher = Application.Current.Dispatcher;
        _settings = SettingsStore.Load();
        _selectedResolution = CaptureResolutions.FindOrDefault(_settings.ResolutionWidth, _settings.ResolutionHeight);

        string cascadePath = Path.Combine(AppContext.BaseDirectory, "Assets", "haarcascade_frontalface_default.xml");
        _videoEngine = new VideoEngine(cascadePath);
        _videoEngine.FrameReady += OnFrameReady;
        _videoEngine.CalibrationProgressChanged += OnCalibrationProgressChanged;
        _videoEngine.CalibrationCompleted += OnCalibrationCompleted;
        _videoEngine.VirtualCameraStatusChanged += OnVirtualCameraStatusChanged;
        _videoEngine.ResolutionStatusChanged += OnResolutionStatusChanged;
        _videoEngine.ErrorOccurred += OnErrorOccurred;

        if (_settings.LastCalibration is { IsCalibrated: true } savedCalibration)
        {
            _videoEngine.ApplySavedParameters(savedCalibration);
            CalibrationStatusText = "Загружены параметры с прошлого запуска. Можно откалибровать заново в любой момент.";
            CalibrationProgress = 100;
            DarkSceneHintVisible = savedCalibration.SceneWasDark;
        }

        ToggleStreamCommand = new RelayCommand(ToggleStream, () => SelectedCamera is not null);
        AutoTuneCommand = new RelayCommand(RunAutoTune, () => IsStreaming && !IsCalibrating);
        InstallVirtualCameraCommand = new RelayCommand(() => _ = InstallVirtualCameraAsync(), () => !IsInstallingVirtualCamera);
        ResetCameraControlsCommand = new RelayCommand(ResetCameraControls, () => CameraControls.Count > 0);

        RefreshCameras();
    }

    public ObservableCollection<CameraDeviceInfo> Cameras { get; } = new();

    /// <summary>Resolution presets offered in the UI, from SD up to 4K UHD.</summary>
    public ObservableCollection<CaptureResolution> Resolutions { get; } = new(CaptureResolutions.Presets);

    /// <summary>Live hardware camera property sliders (brightness, exposure, white balance, etc.) for the currently streaming device.</summary>
    public ObservableCollection<CameraControlSliderViewModel> CameraControls { get; } = new();

    public CameraDeviceInfo? SelectedCamera
    {
        get => _selectedCamera;
        set
        {
            bool changed = SetProperty(ref _selectedCamera, value);
            RelayCommand.RaiseCanExecuteChanged();

            if (!changed)
            {
                return;
            }

            if (value is not null)
            {
                _settings.SelectedCameraName = value.Name;
                PersistSettings();
            }

            if (IsStreaming && value is not null)
            {
                _videoEngine.Start(value.Index, SelectedResolution.Width, SelectedResolution.Height);
                RefreshHardwareControls(value.Index);
            }
        }
    }

    /// <summary>Requested capture resolution (SD through 4K UHD) - the camera may not support every option; see <see cref="ResolutionStatusText"/>.</summary>
    public CaptureResolution SelectedResolution
    {
        get => _selectedResolution;
        set
        {
            if (!SetProperty(ref _selectedResolution, value))
            {
                return;
            }

            _settings.ResolutionWidth = value.Width;
            _settings.ResolutionHeight = value.Height;
            PersistSettings();

            if (IsStreaming && SelectedCamera is not null)
            {
                _videoEngine.Start(SelectedCamera.Index, value.Width, value.Height);
                RefreshHardwareControls(SelectedCamera.Index);
            }
        }
    }

    public BitmapSource? PreviewFrame
    {
        get => _previewFrame;
        private set => SetProperty(ref _previewFrame, value);
    }

    public bool IsStreaming
    {
        get => _isStreaming;
        private set
        {
            if (SetProperty(ref _isStreaming, value))
            {
                OnPropertyChanged(nameof(StreamButtonText));
                RelayCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsCalibrating
    {
        get => _isCalibrating;
        private set
        {
            if (SetProperty(ref _isCalibrating, value))
            {
                RelayCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public double CalibrationProgress
    {
        get => _calibrationProgress;
        private set => SetProperty(ref _calibrationProgress, value);
    }

    public string CalibrationStatusText
    {
        get => _calibrationStatusText;
        private set => SetProperty(ref _calibrationStatusText, value);
    }

    public string VirtualCameraStatusText
    {
        get => _virtualCameraStatusText;
        private set => SetProperty(ref _virtualCameraStatusText, value);
    }

    public string VirtualCameraInstallStatusText
    {
        get => _virtualCameraInstallStatusText;
        private set => SetProperty(ref _virtualCameraInstallStatusText, value);
    }

    public bool IsInstallingVirtualCamera
    {
        get => _isInstallingVirtualCamera;
        private set
        {
            if (SetProperty(ref _isInstallingVirtualCamera, value))
            {
                RelayCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>What resolution/FPS the camera actually negotiated, which may differ from what was requested.</summary>
    public string ResolutionStatusText
    {
        get => _resolutionStatusText;
        private set => SetProperty(ref _resolutionStatusText, value);
    }

    /// <summary>
    /// Quick brightness nudge (-60..+60) applied instantly on top of the calibrated result
    /// (or on its own, before the first calibration) - no need to rerun the 15-second wizard
    /// just to make the picture a bit brighter or dimmer.
    /// </summary>
    public double ManualBrightnessOffset
    {
        get => _manualBrightnessOffset;
        set
        {
            if (!SetProperty(ref _manualBrightnessOffset, value))
            {
                return;
            }

            _videoEngine.SetManualBrightnessOffset(value);
        }
    }

    /// <summary>True after a calibration where the measured face was quite dark - suggests raising hardware Gain/Exposure first.</summary>
    public bool DarkSceneHintVisible
    {
        get => _darkSceneHintVisible;
        private set => SetProperty(ref _darkSceneHintVisible, value);
    }

    public string? ErrorText
    {
        get => _errorText;
        private set => SetProperty(ref _errorText, value);
    }

    public string StreamButtonText => IsStreaming ? "Остановить поток" : "Включить поток";

    public RelayCommand ToggleStreamCommand { get; }
    public RelayCommand AutoTuneCommand { get; }
    public RelayCommand InstallVirtualCameraCommand { get; }
    public RelayCommand ResetCameraControlsCommand { get; }

    private void RefreshCameras()
    {
        Cameras.Clear();
        List<string> names = DirectShowInterop.EnumerateVideoInputDeviceNames();
        for (int i = 0; i < names.Count; i++)
        {
            Cameras.Add(new CameraDeviceInfo(i, names[i]));
        }

        CameraDeviceInfo? preferred = _settings.SelectedCameraName is { } savedName
            ? Cameras.FirstOrDefault(c => c.Name == savedName)
            : null;

        SelectedCamera = preferred ?? (Cameras.Count > 0 ? Cameras[0] : null);

        if (Cameras.Count == 0)
        {
            ErrorText = "Камеры не найдены. Подключите веб-камеру и перезапустите приложение.";
        }
    }

    private void ToggleStream()
    {
        if (IsStreaming)
        {
            _videoEngine.Stop();
            IsStreaming = false;
            IsCalibrating = false;
            PreviewFrame = null;
            ResolutionStatusText = "";
            ClearHardwareControls();
            return;
        }

        if (SelectedCamera is null)
        {
            return;
        }

        ErrorText = null;
        _videoEngine.Start(SelectedCamera.Index, SelectedResolution.Width, SelectedResolution.Height);
        IsStreaming = true;
        RefreshHardwareControls(SelectedCamera.Index);
    }

    private void RunAutoTune()
    {
        try
        {
            ErrorText = null;
            DarkSceneHintVisible = false;
            _videoEngine.RequestCalibration();
            IsCalibrating = true;
        }
        catch (InvalidOperationException ex)
        {
            ErrorText = ex.Message;
        }
    }

    /// <summary>
    /// Opens (or reopens, for a newly selected device) the hardware property interfaces and
    /// rebuilds the slider list from whatever the driver reports supporting. Runs entirely on
    /// the UI thread, matching the COM apartment <see cref="DirectShowInterop"/> already uses.
    /// </summary>
    private void RefreshHardwareControls(int deviceIndex)
    {
        ClearHardwareControls();

        _hardwareController = HardwareCameraController.TryOpen(deviceIndex);
        if (_hardwareController is not null)
        {
            foreach (CameraControlSetting setting in _hardwareController.EnumerateSettings())
            {
                CameraControls.Add(new CameraControlSliderViewModel(_hardwareController, setting));
            }
        }

        RelayCommand.RaiseCanExecuteChanged();
    }

    private void ClearHardwareControls()
    {
        CameraControls.Clear();
        _hardwareController?.Dispose();
        _hardwareController = null;
        RelayCommand.RaiseCanExecuteChanged();
    }

    private void ResetCameraControls()
    {
        foreach (CameraControlSliderViewModel control in CameraControls)
        {
            control.ResetToDefault();
        }
    }

    private async Task InstallVirtualCameraAsync()
    {
        IsInstallingVirtualCamera = true;
        VirtualCameraInstallStatusText = "Скачиваю и устанавливаю драйвер...";

        (bool _, string message) = await VirtualCameraInstaller.InstallAsync();

        VirtualCameraInstallStatusText = message;
        IsInstallingVirtualCamera = false;
    }

    private void PersistSettings() => SettingsStore.Save(_settings);

    private void OnFrameReady(object? sender, BitmapSource frame) =>
        _dispatcher.BeginInvoke(() => PreviewFrame = frame);

    private void OnCalibrationProgressChanged(object? sender, CalibrationProgressEventArgs e) =>
        _dispatcher.BeginInvoke(() =>
        {
            CalibrationProgress = e.ProgressPercent;
            CalibrationStatusText = e.StatusMessage;
            IsCalibrating = e.Phase is CalibrationPhase.WhiteBalance
                or CalibrationPhase.FaceExposure
                or CalibrationPhase.NoiseReduction;
        });

    private void OnCalibrationCompleted(object? sender, CalibrationParameters parameters) =>
        _dispatcher.BeginInvoke(() =>
        {
            IsCalibrating = false;
            CalibrationProgress = 100;
            CalibrationStatusText = "Автонастройка завершена — параметры применены к видеопотоку.";
            DarkSceneHintVisible = parameters.SceneWasDark;

            _settings.LastCalibration = parameters;
            PersistSettings();
        });

    private void OnVirtualCameraStatusChanged(object? sender, string status) =>
        _dispatcher.BeginInvoke(() => VirtualCameraStatusText = status);

    private void OnResolutionStatusChanged(object? sender, string status) =>
        _dispatcher.BeginInvoke(() => ResolutionStatusText = status);

    private void OnErrorOccurred(object? sender, Exception ex) =>
        _dispatcher.BeginInvoke(() =>
        {
            ErrorText = ex.Message;
            IsStreaming = false;
            IsCalibrating = false;
        });

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _hardwareController?.Dispose();
        _videoEngine.Dispose();
        _disposed = true;
    }
}
