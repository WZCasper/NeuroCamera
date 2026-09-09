using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using NeuroCamera.Common;
using NeuroCamera.Engine;
using NeuroCamera.Interop;
using NeuroCamera.Models;

namespace NeuroCamera.ViewModels;

/// <summary>
/// UI-facing state and commands for <c>MainWindow</c>. Owns the <see cref="VideoEngine"/> and
/// marshals every one of its background-thread events onto the WPF Dispatcher before touching
/// any bindable property, so the view model itself is safe to bind directly from XAML.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly VideoEngine _videoEngine;
    private readonly System.Windows.Threading.Dispatcher _dispatcher;

    private CameraDeviceInfo? _selectedCamera;
    private BitmapSource? _previewFrame;
    private bool _isStreaming;
    private bool _isCalibrating;
    private double _calibrationProgress;
    private string _calibrationStatusText = "Нажмите «Автонастройка», чтобы откалибровать камеру за 15 секунд.";
    private string _virtualCameraStatusText = "Виртуальная камера не подключена.";
    private string? _errorText;
    private bool _disposed;

    public MainViewModel()
    {
        _dispatcher = Application.Current.Dispatcher;

        string cascadePath = Path.Combine(AppContext.BaseDirectory, "Assets", "haarcascade_frontalface_default.xml");
        _videoEngine = new VideoEngine(cascadePath);
        _videoEngine.FrameReady += OnFrameReady;
        _videoEngine.CalibrationProgressChanged += OnCalibrationProgressChanged;
        _videoEngine.CalibrationCompleted += OnCalibrationCompleted;
        _videoEngine.VirtualCameraStatusChanged += OnVirtualCameraStatusChanged;
        _videoEngine.ErrorOccurred += OnErrorOccurred;

        ToggleStreamCommand = new RelayCommand(ToggleStream, () => SelectedCamera is not null);
        AutoTuneCommand = new RelayCommand(RunAutoTune, () => IsStreaming && !IsCalibrating);

        RefreshCameras();
    }

    public ObservableCollection<CameraDeviceInfo> Cameras { get; } = new();

    public CameraDeviceInfo? SelectedCamera
    {
        get => _selectedCamera;
        set
        {
            bool changed = SetProperty(ref _selectedCamera, value);
            RelayCommand.RaiseCanExecuteChanged();

            if (changed && IsStreaming && value is not null)
            {
                _videoEngine.Start(value.Index);
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

    public string? ErrorText
    {
        get => _errorText;
        private set => SetProperty(ref _errorText, value);
    }

    public string StreamButtonText => IsStreaming ? "Остановить поток" : "Включить поток";

    public RelayCommand ToggleStreamCommand { get; }
    public RelayCommand AutoTuneCommand { get; }

    private void RefreshCameras()
    {
        Cameras.Clear();
        List<string> names = DirectShowInterop.EnumerateVideoInputDeviceNames();
        for (int i = 0; i < names.Count; i++)
        {
            Cameras.Add(new CameraDeviceInfo(i, names[i]));
        }

        SelectedCamera = Cameras.Count > 0 ? Cameras[0] : null;

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
            return;
        }

        if (SelectedCamera is null)
        {
            return;
        }

        ErrorText = null;
        _videoEngine.Start(SelectedCamera.Index);
        IsStreaming = true;
    }

    private void RunAutoTune()
    {
        try
        {
            ErrorText = null;
            _videoEngine.RequestCalibration();
            IsCalibrating = true;
        }
        catch (InvalidOperationException ex)
        {
            ErrorText = ex.Message;
        }
    }

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
        });

    private void OnVirtualCameraStatusChanged(object? sender, string status) =>
        _dispatcher.BeginInvoke(() => VirtualCameraStatusText = status);

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

        _videoEngine.Dispose();
        _disposed = true;
    }
}
