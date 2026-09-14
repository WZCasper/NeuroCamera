using System.Windows;
using NeuroCamera.Common;

namespace NeuroCamera.ViewModels;

/// <summary>One labeled OBS-equivalent value with its own copy-to-clipboard button.</summary>
public sealed class ObsValueRowViewModel
{
    public ObsValueRowViewModel(string label, string value)
    {
        Label = label;
        Value = value;
        CopyCommand = new RelayCommand(() => Clipboard.SetText(value));
    }

    public string Label { get; }
    public string Value { get; }
    public RelayCommand CopyCommand { get; }
}
