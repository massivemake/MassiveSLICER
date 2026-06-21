using MassiveSlicer.ViewModels;

namespace MassiveSlicer.App.Console;

/// <summary>A typed console command with aliases and metadata for autocomplete.</summary>
public sealed class ConsoleCommandDefinition
{
    public required string Name { get; init; }
    public string[] Aliases { get; init; } = [];
    public required string Description { get; init; }
    public string Usage { get; init; } = "";
    public required Action<ConsoleCommandContext, string> Execute { get; init; }

    public IEnumerable<string> AllNames => [Name, ..Aliases];
}

/// <summary>Execution context passed to console commands.</summary>
public sealed class ConsoleCommandContext
{
    public required MainWindowViewModel Main { get; init; }
    public required Action<string> Log { get; init; }
    public required Action<string> LogError { get; init; }
    public required Action RequestOpenWorkspacePicker { get; init; }
    public required Action RequestSaveWorkspaceAs { get; init; }
    public required Action RequestOpenModelPicker { get; init; }
    public required Action RequestImportKrlPicker { get; init; }
    public required Action RequestPreferencesDialog { get; init; }
}

/// <summary>Autocomplete row shown while typing in the console.</summary>
public sealed class ConsoleCommandSuggestion
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public string Usage { get; init; } = "";
}