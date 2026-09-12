using NeuroCamera.Common;
using NeuroCamera.Engine;
using NeuroCamera.Models;

namespace NeuroCamera.ViewModels;

/// <summary>
/// Bindable wrapper around one <see cref="CameraControlSetting"/>: dragging <see cref="Value"/>
/// applies it to the physical camera as a manual value (and drops out of auto mode); toggling
/// <see cref="IsAuto"/> switches the property back to the driver's automatic control.
/// </summary>
public sealed class CameraControlSliderViewModel : ObservableObject
{
    private readonly HardwareCameraController _controller;
    private readonly CameraControlSetting _setting;

    private int _value;
    private bool _isAuto;

    public CameraControlSliderViewModel(HardwareCameraController controller, CameraControlSetting setting)
    {
        _controller = controller;
        _setting = setting;
        _value = setting.CurrentValue;
        _isAuto = setting.CurrentIsAuto;
    }

    public string DisplayName => _setting.DisplayName;
    public int Min => _setting.Min;
    public int Max => _setting.Max;
    public int Step => _setting.Step;
    public int DefaultValue => _setting.DefaultValue;
    public bool SupportsAuto => _setting.SupportsAuto;

    /// <summary>Current numeric value, shown next to the slider exactly as the driver reports it (same units OBS shows).</summary>
    public int Value
    {
        get => _value;
        set
        {
            if (!SetProperty(ref _value, value))
            {
                return;
            }

            _controller.Set(_setting, value, auto: false);

            if (_isAuto)
            {
                _isAuto = false;
                OnPropertyChanged(nameof(IsAuto));
            }
        }
    }

    public bool IsAuto
    {
        get => _isAuto;
        set
        {
            if (!SetProperty(ref _isAuto, value))
            {
                return;
            }

            _controller.Set(_setting, _value, auto: value);
        }
    }

    /// <summary>
    /// Restores this property to how the driver reports its factory-default state: if the
    /// property supports automatic control (most cameras default exposure/white-balance to
    /// auto), switches it back to Auto; otherwise sets it to the driver-reported default
    /// numeric value.
    /// </summary>
    public void ResetToDefault()
    {
        if (SupportsAuto)
        {
            IsAuto = true;
        }
        else
        {
            Value = DefaultValue;
        }
    }
}
