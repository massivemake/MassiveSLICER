using MassiveSlicer.Commands;

namespace MassiveSlicer.App.Console;

/// <summary>Registers and executes MassiveSlicer console commands.</summary>
public sealed class ConsoleCommandRegistry
{
    private readonly List<ConsoleCommandDefinition> _commands = [];
    private readonly Dictionary<string, ConsoleCommandDefinition> _lookup = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ConsoleCommandDefinition> Commands => _commands;

    public ConsoleCommandRegistry()
    {
        Register(new ConsoleCommandDefinition
        {
            Name = "help",
            Aliases = ["?", "commands"],
            Description = "List available commands",
            Usage = "help [filter]",
            Execute = (ctx, args) =>
            {
                var filter = args.Trim();
                var matches = string.IsNullOrWhiteSpace(filter)
                    ? _commands
                    : _commands.Where(c => MatchesFilter(c, filter)).ToList();

                if (matches.Count == 0)
                {
                    ctx.LogError($"No commands match '{filter}'.");
                    return;
                }

                ctx.Log("Available commands:");
                foreach (var cmd in matches.OrderBy(c => c.Name))
                {
                    var usage = string.IsNullOrWhiteSpace(cmd.Usage) ? cmd.Name : cmd.Usage;
                    ctx.Log($"  {usage,-22} {cmd.Description}");
                }
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "clear",
            Aliases = ["cls"],
            Description = "Clear console history",
            Execute = (ctx, _) => ctx.Main.Console.ClearHistory(),
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "new",
            Aliases = ["new-workspace"],
            Description = "Start a new empty workspace",
            Execute = (ctx, _) => ctx.Main.NewWorkspace(),
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "open",
            Aliases = ["load"],
            Description = "Open a .mass workspace file",
            Usage = "open [path]",
            Execute = (ctx, args) =>
            {
                var path = args.Trim().Trim('"');
                if (string.IsNullOrWhiteSpace(path))
                {
                    ctx.RequestOpenWorkspacePicker();
                    return;
                }

                ctx.Main.OpenWorkspace(path);
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "save-as",
            Aliases = ["saveas", "save as"],
            Description = "Save workspace to a new file",
            Usage = "save-as [path]",
            Execute = (ctx, args) =>
            {
                var path = args.Trim().Trim('"');
                if (string.IsNullOrWhiteSpace(path))
                {
                    ctx.RequestSaveWorkspaceAs();
                    return;
                }

                ctx.Main.SaveWorkspace(path);
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "save",
            Description = "Save workspace to the current file",
            Execute = (ctx, _) =>
            {
                if (!ctx.Main.TrySaveCurrentWorkspace())
                    ctx.RequestSaveWorkspaceAs();
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "settings",
            Aliases = ["preferences", "prefs"],
            Description = "Open application preferences",
            Execute = (ctx, _) => ctx.RequestPreferencesDialog(),
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "panel-settings",
            Aliases = ["panel"],
            Description = "Show the right-panel Settings tab",
            Execute = (ctx, _) =>
            {
                ctx.Main.RightPanel.ShowSettingsCommand.Execute(null);
                ctx.Log("[panel] Settings tab opened.");
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "import",
            Aliases = ["import-model", "model"],
            Description = "Import a 3D model (.glb, .stl, .obj, .3mf)",
            Usage = "import [path]",
            Execute = (ctx, args) =>
            {
                var path = args.Trim().Trim('"');
                if (string.IsNullOrWhiteSpace(path))
                {
                    ctx.RequestOpenModelPicker();
                    return;
                }

                ctx.Main.ImportModelFromPath(path);
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "import-krl",
            Aliases = ["krl", "import krl"],
            Description = "Import a KRL program",
            Execute = (ctx, _) => ctx.RequestImportKrlPicker(),
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "undo",
            Description = "Undo the last change",
            Execute = (ctx, _) =>
            {
                if (ctx.Main.Toolbar.CanUndo)
                    ctx.Main.Toolbar.UndoCommand.Execute(null);
                else
                    ctx.LogError("Nothing to undo.");
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "redo",
            Description = "Redo the last undone change",
            Execute = (ctx, _) =>
            {
                if (ctx.Main.Toolbar.CanRedo)
                    ctx.Main.Toolbar.RedoCommand.Execute(null);
                else
                    ctx.LogError("Nothing to redo.");
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "console",
            Description = "Toggle the console panel",
            Execute = (ctx, _) => ctx.Main.Toolbar.ToggleConsoleCommand.Execute(null),
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "right-panel",
            Aliases = ["sidebar"],
            Description = "Toggle the right settings panel",
            Execute = (ctx, _) => ctx.Main.Toolbar.ToggleRightPanelCommand.Execute(null),
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "frame",
            Aliases = ["frame-all"],
            Description = "Frame all scene objects in the viewport",
            Execute = (ctx, _) => ctx.Main.Toolbar.FrameAllCommand.Execute(null),
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "slice",
            Aliases = ["generate-slice"],
            Description = "Slice the selected mesh into toolpaths",
            Execute = (ctx, _) =>
            {
                var slice = ctx.Main.Viewport.SliceCommand;
                if (slice.CanExecute(null))
                {
                    slice.Execute(null);
                    ctx.Log("[slice] slicing selected mesh...");
                }
                else
                {
                    ctx.LogError("Select a mesh first (e.g. `import <path>` auto-selects it), then run `slice`.");
                }
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "prepare",
            Description = "Switch to Prepare mode",
            Execute = (ctx, _) =>
            {
                ctx.Main.Toolbar.SetPrepareModeCommand.Execute(null);
                ctx.Log("[mode] Prepare");
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "preview",
            Description = "Switch to Preview mode",
            Execute = (ctx, _) =>
            {
                ctx.Main.Toolbar.SetPreviewModeCommand.Execute(null);
                ctx.Log("[mode] Preview");
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "bed-orient",
            Aliases = ["bed-orientation"],
            Description = "Set the rotary bed orientation offset (deg about its vertical axis) and reload",
            Usage = "bed-orient <deg>",
            Execute = (ctx, args) =>
            {
                if (!float.TryParse(args.Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var deg))
                {
                    ctx.LogError("usage: bed-orient <deg>   e.g.  bed-orient -0.93");
                    return;
                }
                ctx.Log($"[bed] {ctx.Main.SetBedOrientationOffset(deg)}");
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "diag-scans",
            Aliases = ["export-scans", "diag scans"],
            Description = "Export this session's rotary scans (world XYZ + E1) for offline calibration analysis",
            Execute = (ctx, _) => ctx.Log($"[diag] {ctx.Main.ExportScanDiagnostics()}"),
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "reload-cell",
            Aliases = ["reload cell", "refresh-cell"],
            Description = "Invalidate cache and reload the active cell scene",
            Execute = (ctx, _) =>
            {
                var path = ctx.Main.Viewport.ActiveCellPath;
                if (path is null)
                {
                    ctx.LogError("[cell] No active cell to reload.");
                    return;
                }

                CellSceneCache.Invalidate(path);
                ctx.Log($"[cell] reloading {Path.GetFileNameWithoutExtension(path)}…");
                ctx.Main.Viewport.OnDevCellReloadRequested?.Invoke(path);
            },
        });
    }

    public bool TryExecute(string line, ConsoleCommandContext ctx)
    {
        line = line.Trim();
        if (line.Length == 0)
            return false;

        if (!TryParse(line, out var command, out var args))
        {
            ctx.LogError($"Unknown command '{GetFirstToken(line)}'. Type 'help' for available commands.");
            return false;
        }

        try
        {
            command.Execute(ctx, args);
        }
        catch (Exception ex)
        {
            ctx.LogError($"Command failed: {ex.Message}");
        }

        return true;
    }

    public IReadOnlyList<ConsoleCommandSuggestion> GetSuggestions(string input)
    {
        input = input.TrimStart();
        if (input.Length == 0)
        {
            return _commands
                .OrderBy(c => c.Name)
                .Select(ToSuggestion)
                .ToList();
        }

        var token = GetFirstToken(input);
        var hasTrailingSpace = input.EndsWith(' ') || input.EndsWith('\t');
        if (hasTrailingSpace)
        {
            var command = ResolveCommand(token);
            if (command is null)
                return [];

            return
            [
                new ConsoleCommandSuggestion
                {
                    Name = command.Name,
                    Description = command.Description,
                    Usage = command.Usage,
                },
            ];
        }

        return _commands
            .Where(c => c.AllNames.Any(n => n.StartsWith(token, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(c => c.Name)
            .Select(ToSuggestion)
            .ToList();
    }

    public string? GetCompletion(string input, int selectedIndex)
    {
        var suggestions = GetSuggestions(input);
        if (suggestions.Count == 0)
            return null;

        var pick = selectedIndex >= 0 && selectedIndex < suggestions.Count
            ? suggestions[selectedIndex]
            : suggestions[0];

        var token = GetFirstToken(input.TrimStart());
        var rest = input.TrimStart();
        var tokenEnd = rest.Length;
        if (token.Length > 0)
        {
            var idx = rest.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
                tokenEnd = idx + token.Length;
        }

        var suffix = rest.Length > tokenEnd ? rest[tokenEnd..] : "";
        return pick.Name + suffix;
    }

    private void Register(ConsoleCommandDefinition command)
    {
        _commands.Add(command);
        foreach (var name in command.AllNames)
            _lookup[name] = command;
    }

    private bool TryParse(string line, out ConsoleCommandDefinition command, out string args)
    {
        command = null!;
        args = "";

        line = line.Trim();
        if (line.Length == 0)
            return false;

        foreach (var candidate in _commands.OrderByDescending(c => c.Name.Length))
        {
            foreach (var name in candidate.AllNames.OrderByDescending(n => n.Length))
            {
                if (line.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    command = candidate;
                    return true;
                }

                if (line.StartsWith(name, StringComparison.OrdinalIgnoreCase)
                    && line.Length > name.Length
                    && char.IsWhiteSpace(line[name.Length]))
                {
                    command = candidate;
                    args = line[(name.Length + 1)..].Trim();
                    return true;
                }
            }
        }

        return false;
    }

    private ConsoleCommandDefinition? ResolveCommand(string token)
        => string.IsNullOrWhiteSpace(token) ? null : _lookup.GetValueOrDefault(token);

    private static string GetFirstToken(string input)
    {
        input = input.TrimStart();
        var i = 0;
        while (i < input.Length && !char.IsWhiteSpace(input[i]))
            i++;
        return input[..i];
    }

    private static bool MatchesFilter(ConsoleCommandDefinition command, string filter)
        => command.AllNames.Any(n => n.Contains(filter, StringComparison.OrdinalIgnoreCase))
           || command.Description.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private static ConsoleCommandSuggestion ToSuggestion(ConsoleCommandDefinition command)
        => new()
        {
            Name = command.Name,
            Description = command.Description,
            Usage = string.IsNullOrWhiteSpace(command.Usage) ? command.Name : command.Usage,
        };
}