using System.Collections.ObjectModel;
using System.Windows.Input;
using MassiveSlicer.Commands;
using MassiveSlicer.ViewModels.Base;

namespace MassiveSlicer.ViewModels;

/// <summary>
/// Backs the floating command console overlay. Maintains the command
/// history and routes input to the command interpreter.
/// </summary>
public sealed class ConsoleViewModel : ViewModelBase
{
    private string _inputText = string.Empty;

    /// <summary>The text currently typed in the console input field.</summary>
    public string InputText
    {
        get => _inputText;
        set => SetField(ref _inputText, value);
    }

    /// <summary>
    /// The ordered list of previous entries shown in the history area.
    /// Each entry is a plain string; the UI adds the prompt character (▶).
    /// </summary>
    public ObservableCollection<string> History { get; } = [];

    /// <summary>Submits <see cref="InputText"/> as a command.</summary>
    public ICommand SubmitCommand { get; }

    /// <summary>Clears the <see cref="History"/> list.</summary>
    public ICommand ClearCommand { get; }

    /// <summary>Initialises commands.</summary>
    public ConsoleViewModel()
    {
        SubmitCommand = new RelayCommand(Submit, () => !string.IsNullOrWhiteSpace(InputText));
        ClearCommand  = new RelayCommand(History.Clear);
    }

    private void Submit()
    {
        if (string.IsNullOrWhiteSpace(InputText))
            return;

        History.Add(InputText.Trim());
        InputText = string.Empty;
        // TODO: route to command interpreter
    }
}
