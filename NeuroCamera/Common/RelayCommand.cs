using System.Windows.Input;

namespace NeuroCamera.Common;

/// <summary>Standard synchronous ICommand implementation backed by delegates.</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => _execute();

    /// <summary>Forces WPF to re-evaluate <see cref="CanExecute"/> for every bound command.</summary>
    public static void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();
}
