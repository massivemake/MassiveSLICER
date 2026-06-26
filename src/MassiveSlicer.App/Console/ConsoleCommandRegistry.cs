using MassiveSlicer.Commands;
using MassiveSlicer.Core.Models;

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
            Name = "move-pose",
            Aliases = ["move-ptp"],
            Description = "PTP the tool to a Cartesian pose via MASSIVE_SERVER",
            Usage = "move-pose <x> <y> <z> [a b c] [vel%]",
            Execute = (ctx, args) => RunServerMove(ctx, args, linear: false),
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "move-lin",
            Description = "LIN the tool to a Cartesian pose via MASSIVE_SERVER",
            Usage = "move-lin <x> <y> <z> [a b c] [vel%]",
            Execute = (ctx, args) => RunServerMove(ctx, args, linear: true),
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "move-home",
            Description = "Send the robot HOME via MASSIVE_SERVER",
            Usage = "move-home [vel%]",
            Execute = (ctx, args) =>
            {
                int vel = int.TryParse(args.Trim(), out var v) ? v : 20;
                _ = ctx.Main.MoveServerHomeAsync(vel);
            },
        });

        RegisterRelativeMove("move-up", ["up"], "Up (+Z)", dzMm: +1);
        RegisterRelativeMove("move-down", ["down"], "Down (−Z)", dzMm: -1);
        RegisterRelativeMove("move-forward", ["forward", "fwd"], "Forward (+X)", dxMm: +1);
        RegisterRelativeMove("move-back", ["back", "backward", "bwd"], "Back (−X)", dxMm: -1);
        RegisterRelativeMove("move-right", ["right"], "Right (+Y)", dyMm: +1);
        RegisterRelativeMove("move-left", ["left"], "Left (−Y)", dyMm: -1);

        Register(new ConsoleCommandDefinition
        {
            Name = "move",
            Description = "Relative jog: move <up|down|forward|back|right|left> [distance] [vel%]",
            Usage = "move up 1'   move forward 12in 15   move down 100mm",
            Execute = (ctx, args) => RunRelativeMovePhrase(ctx, args),
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "pos",
            Aliases = ["where", "tcp", "pose"],
            Description = "Print the live robot TCP pose + a ready-to-paste move-pose line",
            Execute = (ctx, _) => { ctx.Main.LogCurrentPoseAsync(); },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "joints",
            Aliases = ["axis", "axes", "readjoints"],
            Description = "Print $AXIS_ACT (A1–A6, E1) + move-joints line for joint-space planning",
            Execute = (ctx, _) => { ctx.Main.LogCurrentJointsAsync(); },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "move-joints",
            Aliases = ["movejoints", "jmove"],
            Description = "PTP to a joint target via MS_AXIS (use when move-pose hits soft limits)",
            Usage = "move-joints <a1> <a2> <a3> <a4> <a5> <a6> [e1] [vel%] [tool] [base]",
            Execute = (ctx, args) => RunServerJoints(ctx, args),
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "cell",
            Aliases = ["switch-cell"],
            Description = "Switch to a robot cell by name (e.g. cell LFAM 3)",
            Usage = "cell <name>",
            Execute = (ctx, args) =>
            {
                if (string.IsNullOrWhiteSpace(args)) { ctx.LogError("usage: cell <name>   e.g.  cell LFAM 3"); return; }
                ctx.Log(ctx.Main.SwitchCellByName(args));
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "sync",
            Aliases = ["connect", "sync-robot"],
            Description = "Sync (connect) the robot over C3Bridge",
            Execute = (ctx, _) => ctx.Log(ctx.Main.SyncRobot()),
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "desync",
            Aliases = ["disconnect", "desync-robot"],
            Description = "Desync (disconnect) the robot",
            Execute = (ctx, _) => ctx.Log(ctx.Main.DesyncRobot()),
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "set-frame",
            Aliases = ["frame", "setframe"],
            Description = "Apply tool/base on controller without moving (MS_CMD=5)",
            Usage = "set-frame [tool] [base]   default: app LFAM tool/base",
            Execute = (ctx, args) =>
            {
                var p = args.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                int tool = p.Length > 0 && int.TryParse(p[0], System.Globalization.NumberStyles.Integer, inv, out var t)
                    ? t : ctx.Main.RightPanel.Settings.Robot.KrlToolIndex;
                int baseIdx = p.Length > 1 && int.TryParse(p[1], System.Globalization.NumberStyles.Integer, inv, out var b)
                    ? b : ctx.Main.RightPanel.Settings.Robot.KrlBaseIndex;
                ctx.Main.SetServerFrameAsync(tool, baseIdx);
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "scan-pick",
            Aliases = ["scanner-pick", "pick-scanner"],
            Description = "Run Scanner_Pick via CELL (bRunScanPick BOOL trigger)",
            Execute = (ctx, _) => { ctx.Main.TriggerScanPickAsync(); },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "readvar",
            Aliases = ["var", "getvar"],
            Description = "Read one or more KRL variables over C3Bridge",
            Usage = "readvar MS_SEQ MS_ACK MS_CMD MS_STAT MS_BUSY",
            Execute = (ctx, args) =>
            {
                if (string.IsNullOrWhiteSpace(args))
                {
                    ctx.LogError("usage: readvar <name> [name ...]   e.g.  readvar MS_SEQ MS_ACK");
                    return;
                }
                _ = ctx.Main.ReadKrlVarsAsync(args);
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "bed-orient",
            Aliases = ["bed-orientation"],
            Description = "Manual rotary bed orientation offset (deg) — normally set automatically by bed-cal; reloads cell",
            Usage = "bed-orient [deg]   (default −0.97)",
            Execute = (ctx, args) =>
            {
                // Allow "bed-orient -0.97" or accidental "bed-orient =-0.97"; bare command → default.
                var arg = args.Trim().TrimStart('=');
                float deg;
                if (string.IsNullOrWhiteSpace(arg))
                    deg = RotaryBedCellConfig.DefaultOrientationOffsetDeg;
                else if (!float.TryParse(arg, System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out deg))
                {
                    ctx.LogError($"usage: bed-orient [deg]   e.g.  bed-orient   bed-orient {RotaryBedCellConfig.DefaultOrientationOffsetDeg:F2}");
                    return;
                }
                ctx.Log($"[bed] {ctx.Main.SetBedOrientationOffset(deg)}");
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "scan",
            Aliases = ["capture", "zivid"],
            Description = "Capture a Zivid scan — CPU world points stashed for diag export; optional viewport mesh",
            Usage = "scan [cpu-only] [save]",
            Execute = (ctx, args) =>
            {
                var p = args.Split([' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                bool cpuOnly = p.Any(x => x.Equals("cpu-only", StringComparison.OrdinalIgnoreCase)
                                       || x.Equals("cpu", StringComparison.OrdinalIgnoreCase));
                bool save = p.Any(x => x.Equals("save", StringComparison.OrdinalIgnoreCase)
                                    || x.Equals("disk", StringComparison.OrdinalIgnoreCase));
                _ = ctx.Main.RunConsoleScanAsync(addToViewport: !cpuOnly, saveToDisk: save);
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "move-e1",
            Aliases = ["e1", "rotate-bed"],
            Description = "Rotate external axis E1 while holding A1–A6 (MS_AXIS)",
            Usage = "move-e1 <deg> [vel%]",
            Execute = (ctx, args) =>
            {
                var p = args.Split([' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (p.Length < 1 || !double.TryParse(p[0], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var deg))
                {
                    ctx.LogError("usage: move-e1 <deg> [vel%]   e.g.  move-e1 -90  20");
                    return;
                }
                int vel = p.Length >= 2 && int.TryParse(p[1], out var v) ? v : 20;
                _ = ctx.Main.MoveE1Async(deg, vel);
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "bed-cal",
            Aliases = ["bedcal", "auto-bed-cal", "run-bed-cal"],
            Description = "Run Auto Bed Calibration (waypoint → CELL MS_AXIS E1 sweep → fit → BASE_DATA)",
            Execute = (ctx, _) => ctx.Main.StartBedCalibration(),
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "scan-cal",
            Aliases = ["scancal", "auto-scan-cal", "run-scan-cal"],
            Description = "Run Auto 3D Scan (hand-eye) Calibration (waypoint → CELL MS_AXIS wrist sweep → fit → tool #6)",
            Execute = (ctx, _) => ctx.Main.StartScanCalibration(),
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "diag-scans",
            Aliases = ["export-scans", "diag scans", "export-scan"],
            Description = "Export stashed scan world points (from scan / bed-cal) to scan output/diag/",
            Execute = (ctx, _) => ctx.Log($"[diag] {ctx.Main.ExportScanDiagnostics()}"),
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "screenshot",
            Aliases = ["viewport-shot", "capture-viewport"],
            Description = "Save a PNG of the full app window to %LOCALAPPDATA%/MassiveSlicer/screenshots/",
            Execute = (ctx, _) =>
            {
                ctx.Main.SaveViewportScreenshotAsync().ContinueWith(t =>
                {
                    if (t.IsCompletedSuccessfully)
                        ctx.Log($"[screenshot] {t.Result}");
                    else
                        ctx.LogError("[screenshot] capture failed.");
                }, TaskScheduler.FromCurrentSynchronizationContext());
            },
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

        Register(new ConsoleCommandDefinition
        {
            Name = "waypoint",
            Aliases = ["wp", "goto"],
            Description = "List, recall, or save reusable cell waypoints (scan/bed cal, etc.)",
            Usage = "waypoint list | waypoint go <name> [vel%] | waypoint save <name>",
            Execute = (ctx, args) => RunWaypoint(ctx, args),
        });
    }

    private static void RunWaypoint(ConsoleCommandContext ctx, string args)
    {
        var parts = (args ?? string.Empty).Split([' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            ctx.LogError("usage: waypoint list | waypoint go <name> [vel%] | waypoint save <name>");
            return;
        }

        var sub = parts[0].ToLowerInvariant();
        switch (sub)
        {
            case "list" or "ls":
                ctx.Main.LogWaypoints();
                break;
            case "go" or "move" or "to":
                if (parts.Length < 2)
                {
                    ctx.LogError("usage: waypoint go <name> [vel%]");
                    return;
                }
                int vel = -1;
                if (parts.Length >= 3 && int.TryParse(parts[2], out var v))
                    vel = v;
                _ = ctx.Main.GoToWaypointAsync(parts[1], vel);
                break;
            case "save" or "add" or "store":
                if (parts.Length < 2)
                {
                    ctx.LogError("usage: waypoint save <name>");
                    return;
                }
                _ = ctx.Main.SaveWaypointFromRobotAsync(parts[1]);
                break;
            default:
                // Shorthand: `waypoint scanner-down-bed` → go
                if (int.TryParse(parts[^1], out var velOnly) && parts.Length >= 2)
                {
                    _ = ctx.Main.GoToWaypointAsync(parts[0], velOnly);
                }
                else
                {
                    _ = ctx.Main.GoToWaypointAsync(parts[0]);
                }
                break;
        }
    }

    private void RegisterRelativeMove(string name, string[] aliases, string description,
        double dxMm = 0, double dyMm = 0, double dzMm = 0)
    {
        Register(new ConsoleCommandDefinition
        {
            Name = name,
            Aliases = aliases,
            Description = $"{description} — distance in ', in, mm (default 1')",
            Usage = $"{name} [1' | 12in | 100mm] [vel%]",
            Execute = (ctx, args) => RunRelativeMove(ctx, args, dxMm, dyMm, dzMm),
        });
    }

    private static void RunRelativeMove(ConsoleCommandContext ctx, string args, double dxSign, double dySign, double dzSign)
    {
        var (distText, vel) = ConsoleDistanceParser.SplitDistanceAndVel(args);
        if (!ConsoleDistanceParser.TryParseToMm(distText, out var mm))
        {
            ctx.LogError($"usage: distance like 1'  12in  100mm  (got '{args.Trim()}')");
            return;
        }
        _ = ctx.Main.MoveRelativeAsync(dxSign * mm, dySign * mm, dzSign * mm, vel);
    }

    private static void RunRelativeMovePhrase(ConsoleCommandContext ctx, string args)
    {
        args = (args ?? string.Empty).Trim();
        if (args.Length == 0)
        {
            ctx.LogError("usage: move <up|down|forward|back|right|left> [1' | 12in | 100mm] [vel%]");
            return;
        }

        var parts = args.Split([' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string dir = parts[0].ToLowerInvariant();
        string rest = parts.Length > 1 ? string.Join(' ', parts[1..]) : string.Empty;

        (double dx, double dy, double dz) = dir switch
        {
            "up" => (0, 0, +1),
            "down" => (0, 0, -1),
            "forward" or "fwd" => (+1, 0, 0),
            "back" or "backward" or "bwd" => (-1, 0, 0),
            "right" => (0, +1, 0),
            "left" => (0, -1, 0),
            _ => (0, 0, 0),
        };

        if (dx == 0 && dy == 0 && dz == 0)
        {
            ctx.LogError($"unknown direction '{parts[0]}' — use up down forward back right left");
            return;
        }

        RunRelativeMove(ctx, rest, dx, dy, dz);
    }

    // Parses "x y z [a b c] [vel%] [tool] [base]" and fires a MS_* Cartesian move.
    private static void RunServerMove(ConsoleCommandContext ctx, string args, bool linear)
    {
        var p = args.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        double D(int i, double def) => i < p.Length && double.TryParse(p[i], System.Globalization.NumberStyles.Float, inv, out var d) ? d : def;
        if (p.Length < 3) { ctx.LogError("usage: move-pose <x> <y> <z> [a b c] [vel%] [tool] [base]"); return; }

        int end = p.Length;
        int tool = -1, baseIdx = -1;
        if (end >= 5
            && int.TryParse(p[end - 1], System.Globalization.NumberStyles.Integer, inv, out var b)
            && int.TryParse(p[end - 2], System.Globalization.NumberStyles.Integer, inv, out var t))
        {
            baseIdx = b;
            tool = t;
            end -= 2;
        }

        double x = D(0, 0), y = D(1, 0), z = D(2, 0);
        double a = 0, bAng = 0, c = 0;
        int vel = 20;
        if (end == 4)
        {
            vel = (int)D(3, 20);
        }
        else if (end >= 6)
        {
            a = D(3, 0); bAng = D(4, 0); c = D(5, 0);
            if (end >= 7) vel = (int)D(6, 20);
        }

        _ = ctx.Main.MoveServerPoseAsync(linear, x, y, z, a, bAng, c, vel, tool, baseIdx);
    }

    private static void RunServerJoints(ConsoleCommandContext ctx, string args)
    {
        var p = args.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        double D(int i) => i < p.Length && double.TryParse(p[i], System.Globalization.NumberStyles.Float, inv, out var d) ? d : 0;
        if (p.Length < 6) { ctx.LogError("usage: move-joints <a1>..<a6> [e1] [vel%] [tool] [base]"); return; }

        int end = p.Length;
        int tool = -1, baseIdx = -1;
        if (end >= 8
            && int.TryParse(p[end - 1], System.Globalization.NumberStyles.Integer, inv, out var b)
            && int.TryParse(p[end - 2], System.Globalization.NumberStyles.Integer, inv, out var t))
        {
            baseIdx = b; tool = t; end -= 2;
        }

        double e1 = 0;
        int vel = 20;
        if (end == 7) vel = (int)D(6);
        else if (end == 8) { e1 = D(6); vel = (int)D(7); }

        ctx.Main.MoveServerJointsAsync(D(0), D(1), D(2), D(3), D(4), D(5), e1, vel, tool, baseIdx);
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