using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Threading;
using MassiveSlicer.App.Console;
using MassiveSlicer.Commands;
using MassiveSlicer.ViewModels.Base;

namespace MassiveSlicer.ViewModels;

/// <summary>
/// Backs the command console. Maintains history, autocomplete suggestions,
/// and routes input to the command registry.
/// </summary>
public sealed class ConsoleViewModel : ViewModelBase
{
    private readonly ConsoleCommandRegistry _registry = new();
    private ConsoleCommandContext? _context;
    private string _inputText = string.Empty;
    private int _selectedSuggestionIndex = -1;
    private int _historyBrowseIndex = -1;
    private string? _historyDraft;

    public string InputText
    {
        get => _inputText;
        set
        {
            if (!SetField(ref _inputText, value))
                return;

            _historyBrowseIndex = -1;
            _historyDraft = null;
            RefreshSuggestions();
            ((RelayCommand)SubmitCommand).RaiseCanExecuteChanged();
        }
    }

    public ObservableCollection<ConsoleHistoryEntry> History { get; } = [];

    public ObservableCollection<ConsoleCommandSuggestion> Suggestions { get; } = [];

    public bool HasSuggestions => Suggestions.Count > 0;

    public int SelectedSuggestionIndex
    {
        get => _selectedSuggestionIndex;
        set => SetField(ref _selectedSuggestionIndex, value);
    }

    public ICommand SubmitCommand { get; }
    public ICommand ClearCommand { get; }

    public ConsoleViewModel()
    {
        SubmitCommand = new RelayCommand(Submit, () => !string.IsNullOrWhiteSpace(InputText));
        ClearCommand  = new RelayCommand(ClearHistory);
    }

    public void Attach(MainWindowViewModel main, ConsoleCommandContext context)
    {
        _context = context;
    }

    public void Log(string message) => AppendHistory(new ConsoleHistoryEntry(message));

    public void LogError(string message) => AppendHistory(new ConsoleHistoryEntry(message, isError: true));

    public void ClearHistory()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            History.Clear();
            History.Add(new ConsoleHistoryEntry("Console cleared."));
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            History.Clear();
            History.Add(new ConsoleHistoryEntry("Console cleared."));
        });
    }

    private void AppendHistory(ConsoleHistoryEntry entry)
    {
        if (Dispatcher.UIThread.CheckAccess())
            History.Add(entry);
        else
            Dispatcher.UIThread.Post(() => History.Add(entry));
    }

    public bool TryCompleteSuggestion()
    {
        if (!HasSuggestions)
            return false;

        var completion = _registry.GetCompletion(InputText, SelectedSuggestionIndex);
        if (completion is null)
            return false;

        InputText = completion + " ";
        SelectedSuggestionIndex = 0;
        return true;
    }

    public bool TryMoveSuggestion(int delta)
    {
        if (!HasSuggestions)
            return false;

        if (SelectedSuggestionIndex < 0)
            SelectedSuggestionIndex = 0;
        else
            SelectedSuggestionIndex = (SelectedSuggestionIndex + delta + Suggestions.Count) % Suggestions.Count;

        return true;
    }

    public bool TryBrowseHistory(int delta)
    {
        var commands = History.Where(h => h.IsCommand).Select(h => h.Text).ToList();
        if (commands.Count == 0)
            return false;

        if (_historyBrowseIndex < 0)
            _historyDraft = InputText;

        var next = _historyBrowseIndex + delta;
        if (next < 0)
        {
            _historyBrowseIndex = -1;
            InputText = _historyDraft ?? string.Empty;
            _historyDraft = null;
            return true;
        }

        if (next >= commands.Count)
            return false;

        _historyBrowseIndex = next;
        InputText = commands[^(_historyBrowseIndex + 1)];
        return true;
    }

    private void Submit()
    {
        if (string.IsNullOrWhiteSpace(InputText))
            return;

        var line = InputText.Trim();
        History.Add(new ConsoleHistoryEntry(line, isCommand: true));
        InputText = string.Empty;
        SelectedSuggestionIndex = -1;
        Suggestions.Clear();
        OnPropertyChanged(nameof(HasSuggestions));

        if (_context is null)
        {
            LogError("Console commands are not wired yet.");
            return;
        }

        _registry.TryExecute(line, _context);
    }

    private void RefreshSuggestions()
    {
        Suggestions.Clear();
        foreach (var suggestion in _registry.GetSuggestions(InputText))
            Suggestions.Add(suggestion);

        SelectedSuggestionIndex = Suggestions.Count > 0 ? 0 : -1;
        OnPropertyChanged(nameof(HasSuggestions));
    }
}