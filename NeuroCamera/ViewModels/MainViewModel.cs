using System.Collections.ObjectModel;
using System.Diagnostics;
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
/// (software correction + preview) and, independently, a <see cref="HardwareCameraController"/>
/// for the selected device's own driver-level properties (brightness/exposure/etc.).
///
/// Автонастройка (auto-tune) is hardware-first: while <see cref="VideoEngine"/> runs its
/// 15-second calibration, this view model listens to the same live per-frame measurements
/// (<see cref="CalibrationProgressEventArgs.FaceLuminance"/> / <see cref="CalibrationProgressEventArgs.NoiseLevel"/>)
/// and drives the real hardware sliders (<see cref="CameraControlSliderViewModel.Value"/>)
/// toward a good result with closed-loop feedback - so the sliders visibly move during
/// calibration and the picture is corrected at the source, not only after capture. A lighter
/// software correction layer still runs on top for whatever the hardware alone cannot reach
/// (see <see cref="CalibrationEngine.HardwareConvergenceSeconds"/> equivalent windowing there).
///
/// Every background-thread event from <see cref="VideoEngine"/> is marshalled onto the WPF
/// Dispatcher before touching any bindable property or hardware control, since
/// <see cref="HardwareCameraController"/> is COM/STA-bound to the UI thread.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const double HardwareTargetLuminance = 140.0;
    private const double HardwareLuminanceDeadband = 6.0;
    private const double OverexposureReportThreshold = 20.0;
    private static readonly TimeSpan HardwareAdjustmentThrottle = TimeSpan.FromMilliseconds(220);

    private readonly VideoEngine _videoEngine;
    private readonly System.Windows.Threading.Dispatcher _dispatcher;
    private readonly AppSettings _settings;

    private HardwareCameraController? _hardwareController;
    private ObsEquivalentCalculator.ObsColorCorrectionValues? _lastObsValues;

    // Resolved hardware "levers" the auto-tune coordinator drives directly - re-resolved every
    // time the camera control list is (re)built, since they depend on what this specific
    // device's driver actually reports supporting.
    private CameraControlSliderViewModel? _brightnessLever;
    private CameraControlSliderViewModel? _gainLever;
    private CameraControlSliderViewModel? _exposureLever;
    private CameraControlSliderViewModel? _sharpnessLever;
    private CameraControlSliderViewModel? _saturationLever;
    private CameraControlSliderViewModel? _whiteBalanceLever;

    private bool _hardwareWhiteBalanceHandled;
    private bool _hardwareSharpnessSaturationHandled;
    private DateTime _lastHardwareExposureAdjustment = DateTime.MinValue;
    private double? _lastMeasuredFaceLuminance;

    private CameraDeviceInfo? _selectedCamera;
    private CaptureResolution _selectedResolution;
    private BitmapSource? _previewFrame;
    private bool _isStreaming;
    private bool _isCalibrating;
    private double _calibrationProgress;
    private string _calibrationStatusText = "Нажмите «Автонастройка», чтобы откалибровать камеру за 15 секунд.";
    private string _resolutionStatusText = "";
    private double _manualBrightnessOffset;
    private bool _darkSceneHintVisible;
    private bool _overexposureHintVisible;
    private string _overexposureHintText = "";
    private string _obsSourceName;
    private string _obsFilterName;
    private string _obsHost;
    private int _obsPort;
    private string _obsStatusText = "";
    private bool _isObsBusy;
    private string? _errorText;
    private bool _disposed;

    public MainViewModel()
    {
        _dispatcher = Application.Current.Dispatcher;
        _settings = SettingsStore.Load();
        _selectedResolution = CaptureResolutions.FindOrDefault(_settings.ResolutionWidth, _settings.ResolutionHeight);
        _obsHost = _settings.ObsHost;
        _obsPort = _settings.ObsPort;
        _obsSourceName = _settings.ObsSourceName;
        _obsFilterName = _settings.ObsFilterName;

        string cascadePath = Path.Combine(AppContext.BaseDirectory, "Assets", "haarcascade_frontalface_default.xml");
        _videoEngine = new VideoEngine(cascadePath);
        _videoEngine.FrameReady += OnFrameReady;
        _videoEngine.CalibrationProgressChanged += OnCalibrationProgressChanged;
        _videoEngine.CalibrationCompleted += OnCalibrationCompleted;
        _videoEngine.ResolutionStatusChanged += OnResolutionStatusChanged;
        _videoEngine.ErrorOccurred += OnErrorOccurred;

        if (_settings.LastCalibration is { IsCalibrated: true } savedCalibration)
        {
            _videoEngine.ApplySavedParameters(savedCalibration);
            CalibrationStatusText = "Загружены параметры с прошлого запуска. Можно откалибровать заново в любой момент.";
            CalibrationProgress = 100;
            DarkSceneHintVisible = savedCalibration.SceneWasDark;
            RecomputeObsEquivalent(savedCalibration);
        }

        ToggleStreamCommand = new RelayCommand(ToggleStream, () => SelectedCamera is not null);
        AutoTuneCommand = new RelayCommand(RunAutoTune, () => IsStreaming && !IsCalibrating);
        ResetCameraControlsCommand = new RelayCommand(ResetCameraControls, () => CameraControls.Count > 0);
        TestObsConnectionCommand = new RelayCommand(() => _ = TestObsConnectionAsync(), () => !IsObsBusy);
        ApplyToObsCommand = new RelayCommand(() => _ = ApplyToObsAsync(), () => !IsObsBusy && _lastObsValues is not null);
        OpenDonateLinkCommand = new RelayCommand(() => OpenUrl("https://dalink.to/wz_casper"));
        OpenDeveloperContactCommand = new RelayCommand(() => OpenUrl("https://t.me/WZ_Casper"));

        RefreshCameras();
    }

    public ObservableCollection<CameraDeviceInfo> Cameras { get; } = new();

    /// <summary>Resolution presets offered in the UI, from SD up to 4K UHD.</summary>
    public ObservableCollection<CaptureResolution> Resolutions { get; } = new(CaptureResolutions.Presets);

    /// <summary>
    /// Live hardware camera property sliders (brightness, exposure, white balance, etc.) for
    /// the currently streaming device. During Автонастройка, the relevant sliders' values are
    /// driven directly by the calibration's live measurements (see class remarks) and move in
    /// real time, exactly reflecting what is being written to the physical camera.
    /// </summary>
    public ObservableCollection<CameraControlSliderViewModel> CameraControls { get; } = new();

    /// <summary>NeuroCamera's current correction expressed as OBS Color Correction filter values, one row per parameter with its own copy button.</summary>
    public ObservableCollection<ObsValueRowViewModel> ObsValueRows { get; } = new();

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
            RecomputeObsEquivalent(_videoEngine.CurrentParameters);
        }
    }

    /// <summary>True after a calibration where the measured face was quite dark before correction - hardware Gain/Exposure alone could not fully compensate for a genuine lack of light.</summary>
    public bool DarkSceneHintVisible
    {
        get => _darkSceneHintVisible;
        private set => SetProperty(ref _darkSceneHintVisible, value);
    }

    /// <summary>True if the face still measured meaningfully brighter than target after calibration finished - names a specific slider to pull down.</summary>
    public bool OverexposureHintVisible
    {
        get => _overexposureHintVisible;
        private set => SetProperty(ref _overexposureHintVisible, value);
    }

    public string OverexposureHintText
    {
        get => _overexposureHintText;
        private set => SetProperty(ref _overexposureHintText, value);
    }

    public string ObsSourceName
    {
        get => _obsSourceName;
        set => SetProperty(ref _obsSourceName, value);
    }

    public string ObsFilterName
    {
        get => _obsFilterName;
        set => SetProperty(ref _obsFilterName, value);
    }

    public string ObsHost
    {
        get => _obsHost;
        set => SetProperty(ref _obsHost, value);
    }

    public int ObsPort
    {
        get => _obsPort;
        set => SetProperty(ref _obsPort, value);
    }

    /// <summary>
    /// OBS WebSocket server password, set from the UI's PasswordBox via code-behind (WPF does
    /// not allow binding PasswordBox.Password directly, for security reasons). Deliberately
    /// never persisted to <see cref="AppSettings"/>.
    /// </summary>
    public string ObsPassword { get; set; } = "";

    public string ObsStatusText
    {
        get => _obsStatusText;
        private set => SetProperty(ref _obsStatusText, value);
    }

    public bool IsObsBusy
    {
        get => _isObsBusy;
        private set
        {
            if (SetProperty(ref _isObsBusy, value))
            {
                RelayCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string? ErrorText
    {
        get => _errorText;
        private set => SetProperty(ref _errorText, value);
    }

    public string StreamButtonText => IsStreaming ? "Остановить поток" : "Включить поток";

    public RelayCommand ToggleStreamCommand { get; }
    public RelayCommand AutoTuneCommand { get; }
    public RelayCommand ResetCameraControlsCommand { get; }
    public RelayCommand TestObsConnectionCommand { get; }
    public RelayCommand ApplyToObsCommand { get; }
    public RelayCommand OpenDonateLinkCommand { get; }
    public RelayCommand OpenDeveloperContactCommand { get; }

    private void RefreshCameras()
    {
        Cameras.Clear();
        foreach (DirectShowInterop.VideoInputDevice device in DirectShowInterop.EnumerateVideoInputDevices())
        {
            Cameras.Add(new CameraDeviceInfo(device.Index, device.Name));
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
            OverexposureHintVisible = false;
            _hardwareWhiteBalanceHandled = false;
            _hardwareSharpnessSaturationHandled = false;
            _lastHardwareExposureAdjustment = DateTime.MinValue;
            _lastMeasuredFaceLuminance = null;

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

        ResolveHardwareLevers();
        RelayCommand.RaiseCanExecuteChanged();
    }

    private void ClearHardwareControls()
    {
        CameraControls.Clear();
        _hardwareController?.Dispose();
        _hardwareController = null;
        ResolveHardwareLevers();
        RelayCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Re-finds the specific sliders the auto-tune coordinator drives, since which properties a device supports is device-specific.</summary>
    private void ResolveHardwareLevers()
    {
        _brightnessLever = FindControl(CameraControlKind.VideoProcAmp, (int)VideoProcAmpProperty.Brightness);
        _gainLever = FindControl(CameraControlKind.VideoProcAmp, (int)VideoProcAmpProperty.Gain);
        _exposureLever = FindControl(CameraControlKind.CameraControl, (int)CameraControlProperty.Exposure);
        _sharpnessLever = FindControl(CameraControlKind.VideoProcAmp, (int)VideoProcAmpProperty.Sharpness);
        _saturationLever = FindControl(CameraControlKind.VideoProcAmp, (int)VideoProcAmpProperty.Saturation);
        _whiteBalanceLever = FindControl(CameraControlKind.VideoProcAmp, (int)VideoProcAmpProperty.WhiteBalance);
    }

    private CameraControlSliderViewModel? FindControl(CameraControlKind kind, int propertyIndex) =>
        CameraControls.FirstOrDefault(c => c.Kind == kind && c.PropertyIndex == propertyIndex);

    private void ResetCameraControls()
    {
        foreach (CameraControlSliderViewModel control in CameraControls)
        {
            control.ResetToDefault();
        }
    }

    /// <summary>
    /// Runs once per calibration phase-1 frame: switches the camera's own White Balance to
    /// Auto if the driver supports it, rather than computing a one-shot Kelvin value from a
    /// single frame - a continuously-adjusting hardware AWB is generally more reliable than a
    /// single closed-form estimate.
    /// </summary>
    private void EnsureHardwareWhiteBalanceAuto()
    {
        if (_hardwareWhiteBalanceHandled)
        {
            return;
        }

        if (_whiteBalanceLever is { SupportsAuto: true } wb && !wb.IsAuto)
        {
            wb.IsAuto = true;
        }

        _hardwareWhiteBalanceHandled = true;
    }

    /// <summary>
    /// Closed-loop proportional control: on every live luminance measurement during the
    /// exposure phase, nudges the first available lever (Brightness, then Gain, then Exposure)
    /// toward the target, re-measuring on the next frame to correct further - rather than
    /// computing a single blind formula, this reacts to what the camera actually produces,
    /// which is far more robust across different camera models' real behavior. Assigning
    /// straight to a CameraControlSliderViewModel.Value both writes the hardware and updates
    /// the visible slider in the same step, so it moves in real time during Автонастройка.
    /// </summary>
    private void AdjustHardwareExposureTowardTarget(double currentLuminance)
    {
        if (DateTime.UtcNow - _lastHardwareExposureAdjustment < HardwareAdjustmentThrottle)
        {
            return;
        }

        double error = HardwareTargetLuminance - currentLuminance;
        if (Math.Abs(error) < HardwareLuminanceDeadband)
        {
            return;
        }

        foreach (CameraControlSliderViewModel? lever in new[] { _brightnessLever, _gainLever, _exposureLever })
        {
            if (lever is null)
            {
                continue;
            }

            double range = Math.Max(lever.Max - lever.Min, 1);
            int step = (int)Math.Round(Math.Clamp(error / 255.0, -0.08, 0.08) * range);
            if (step == 0)
            {
                step = error > 0 ? 1 : -1;
            }

            int newValue = Math.Clamp(lever.Value + step, lever.Min, lever.Max);
            if (newValue != lever.Value)
            {
                lever.Value = newValue;
                _lastHardwareExposureAdjustment = DateTime.UtcNow;
                return;
            }
            // This lever is already at its limit in the needed direction - try the next one.
        }
    }

    /// <summary>
    /// Runs once in the noise-reduction phase: sets Sharpness lower when measured sensor noise
    /// is higher (so sharpening does not amplify grain into visible artifacts) and nudges
    /// Saturation a modest, deliberately small step above the driver's own default - a single
    /// informed adjustment rather than an iterative chase, since both properties are far less
    /// sensitive to lighting changes than exposure.
    /// </summary>
    private void AdjustHardwareSharpnessAndSaturation(double noiseLevel)
    {
        if (_hardwareSharpnessSaturationHandled)
        {
            return;
        }

        if (_sharpnessLever is { } sharpness)
        {
            double range = sharpness.Max - sharpness.Min;
            double fraction = Math.Clamp(0.55 - (noiseLevel / 40.0), 0.25, 0.65);
            int target = sharpness.Min + (int)Math.Round(range * fraction);
            sharpness.Value = Math.Clamp(target, sharpness.Min, sharpness.Max);
        }

        if (_saturationLever is { } saturation)
        {
            double range = saturation.Max - saturation.Min;
            int target = saturation.DefaultValue + (int)Math.Round(range * 0.08);
            saturation.Value = Math.Clamp(target, saturation.Min, saturation.Max);
        }

        _hardwareSharpnessSaturationHandled = true;
    }

    /// <summary>After calibration ends, checks the last live measurement and, if the face is still notably above target, names the currently-highest lever as a concrete manual suggestion.</summary>
    private void UpdateOverexposureHint()
    {
        if (_lastMeasuredFaceLuminance is double luminance && luminance > HardwareTargetLuminance + OverexposureReportThreshold)
        {
            CameraControlSliderViewModel? culprit = FindHighestRelativeLever(_brightnessLever, _gainLever, _exposureLever);
            OverexposureHintText = culprit is not null
                ? $"Лицо всё ещё пересвечено. Попробуйте вручную уменьшить ползунок «{culprit.DisplayName}» в разделе «Ручные настройки камеры»."
                : "Лицо всё ещё пересвечено. Попробуйте вручную уменьшить яркость в разделе «Ручные настройки камеры».";
            OverexposureHintVisible = true;
        }
        else
        {
            OverexposureHintVisible = false;
        }
    }

    private static CameraControlSliderViewModel? FindHighestRelativeLever(params CameraControlSliderViewModel?[] levers)
    {
        CameraControlSliderViewModel? best = null;
        double bestFraction = -1;

        foreach (CameraControlSliderViewModel? lever in levers)
        {
            if (lever is null)
            {
                continue;
            }

            double range = Math.Max(lever.Max - lever.Min, 1);
            double fraction = (lever.Value - lever.Min) / range;
            if (fraction > bestFraction)
            {
                bestFraction = fraction;
                best = lever;
            }
        }

        return best;
    }

    private void RecomputeObsEquivalent(CalibrationParameters baseParameters)
    {
        if (!baseParameters.IsCalibrated && ManualBrightnessOffset == 0)
        {
            _lastObsValues = null;
            ObsValueRows.Clear();
            RelayCommand.RaiseCanExecuteChanged();
            return;
        }

        CalibrationParameters effective = baseParameters with
        {
            IsCalibrated = true,
            Beta = baseParameters.Beta + ManualBrightnessOffset
        };

        ObsEquivalentCalculator.ObsColorCorrectionValues values = ObsEquivalentCalculator.FromCalibration(effective);
        _lastObsValues = values;

        ObsValueRows.Clear();
        ObsValueRows.Add(new ObsValueRowViewModel("Гамма", values.Gamma.ToString("+0.00;-0.00;0.00")));
        ObsValueRows.Add(new ObsValueRowViewModel("Контрастность", values.Contrast.ToString("+0.00;-0.00;0.00")));
        ObsValueRows.Add(new ObsValueRowViewModel("Яркость", values.Brightness.ToString("+0.0000;-0.0000;0.0000")));
        ObsValueRows.Add(new ObsValueRowViewModel("Насыщенность", "0,00 (не меняется)"));
        ObsValueRows.Add(new ObsValueRowViewModel("Сдвиг оттенка", "0,00 (не меняется)"));

        RelayCommand.RaiseCanExecuteChanged();
    }

    private async Task TestObsConnectionAsync()
    {
        IsObsBusy = true;
        ObsStatusText = "Подключаюсь к OBS...";

        (bool _, string message) = await ObsWebSocketClient.TestConnectionAsync(ObsHost, ObsPort, ObsPassword);

        ObsStatusText = message;
        IsObsBusy = false;
    }

    private async Task ApplyToObsAsync()
    {
        if (_lastObsValues is not { } values)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(ObsSourceName))
        {
            ObsStatusText = "Укажите точное имя источника камеры так, как оно называется в списке источников OBS.";
            return;
        }

        IsObsBusy = true;
        ObsStatusText = "Подключаюсь к OBS...";

        (bool success, string message) = await ObsWebSocketClient.ApplyColorCorrectionAsync(
            ObsHost, ObsPort, ObsPassword, ObsSourceName, ObsFilterName, values);

        ObsStatusText = message;
        IsObsBusy = false;

        if (success)
        {
            _settings.ObsHost = ObsHost;
            _settings.ObsPort = ObsPort;
            _settings.ObsSourceName = ObsSourceName;
            _settings.ObsFilterName = ObsFilterName;
            PersistSettings();
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            // Best-effort - nothing more constructive to do if the OS has no URL handler.
        }
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

            switch (e.Phase)
            {
                case CalibrationPhase.WhiteBalance:
                    EnsureHardwareWhiteBalanceAuto();
                    break;

                case CalibrationPhase.FaceExposure:
                    if (e.FaceLuminance is double luminance)
                    {
                        _lastMeasuredFaceLuminance = luminance;
                        AdjustHardwareExposureTowardTarget(luminance);
                    }
                    break;

                case CalibrationPhase.NoiseReduction:
                    if (e.NoiseLevel is double noise)
                    {
                        AdjustHardwareSharpnessAndSaturation(noise);
                    }
                    break;
            }
        });

    private void OnCalibrationCompleted(object? sender, CalibrationParameters parameters) =>
        _dispatcher.BeginInvoke(() =>
        {
            IsCalibrating = false;
            CalibrationProgress = 100;
            CalibrationStatusText = "Автонастройка завершена — параметры применены к видеопотоку.";
            DarkSceneHintVisible = parameters.SceneWasDark;
            UpdateOverexposureHint();
            RecomputeObsEquivalent(parameters);

            _settings.LastCalibration = parameters;
            PersistSettings();
        });

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
