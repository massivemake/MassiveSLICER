namespace MassiveSlicer.ViewModels;

/// <summary>A single line in the console history.</summary>
public sealed class ConsoleHistoryEntry
{
    public string Text { get; }
    public bool IsCommand { get; }
    public bool IsError { get; }

    public string DisplayLine => (IsCommand ? "▶ " : "") + Text;

    public ConsoleHistoryEntry(string text, bool isCommand = false, bool isError = false)
    {
        Text = text;
        IsCommand = isCommand;
        IsError = isError;
    }
}