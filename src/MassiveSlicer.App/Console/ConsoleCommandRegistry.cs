using MassiveSlicer.Commands;
using MassiveSlicer.Core.Models;
using MassiveSlicer.ViewModels;

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
            Name = "seqtest",
            Description = "Debug: select first toolpath, range-extend to last",
            Execute = (ctx, _) =>
            {
                var v = ctx.Main.Viewport;
                var models = v.GetUserModelItems()
                    .Where(m => m.Children.Any(c => c.IsToolpath)).ToList();
                if (models.Count < 2) { ctx.Log($"[seqtest] need 2+ sliced models, have {models.Count}"); return; }
                var firstTp = models[0].Children.First(c => c.IsToolpath);
                v.ForceSelectNode?.Invoke(firstTp.Node);
                var lastTp = models[^1].Children.First(c => c.IsToolpath);
                bool ok = v.TryToggleToolpathSequenceSelection(lastTp);
                ctx.Log($"[seqtest] toggled={ok} selected={v.GetSequenceCount?.Invoke() ?? -1} of {models.Count}");
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "erp",
            Description = "ERP attachment: erp url <u> | token <t> | connect | expand | search <q> | attach <i> [elemIdx] | newelem <name> | sendslice | pricing | quote [qty] [finishing] | detach | status",
            Execute = (ctx, args) =>
            {
                var erp = ctx.Main.Viewport.Erp;
                var parts = args.Trim().Split(' ', 2);
                switch (parts[0].ToLowerInvariant())
                {
                    case "url":     erp.BaseUrl  = parts.ElementAtOrDefault(1)?.Trim() ?? ""; ctx.Log($"[erp] url = {erp.BaseUrl}"); break;
                    case "token":   erp.ApiToken = parts.ElementAtOrDefault(1)?.Trim() ?? ""; ctx.Log("[erp] token set"); break;
                    case "connect": erp.ConnectCommand.Execute(null); break;
                    case "expand":
                    case "toggle":
                        // Force-open the ERP dock (same as clicking the ERP button).
                        try
                        {
                            erp.IsExpanded = true;
                            ctx.Log($"[erp] expanded={erp.IsExpanded} attached='{erp.ToggleLabel}' " +
                                    $"showAtt={erp.ShowAttachment} showSearch={erp.ShowSearch} " +
                                    $"showSettings={erp.ShowSettings} candidates={erp.WorkspaceCandidates.Count} " +
                                    $"pricing='{erp.PricingSummary}' status='{erp.Status}'");
                        }
                        catch (Exception ex)
                        {
                            ctx.Log($"[erp] expand CRASHED: {ex}");
                            throw;
                        }
                        break;
                    case "search":  erp.SearchText = parts.ElementAtOrDefault(1) ?? ""; break;
                    case "attach":
                    {
                        var idx = (parts.ElementAtOrDefault(1) ?? "0").Split(' ');
                        if (int.TryParse(idx[0], out int i) && i >= 0 && i < erp.SearchResults.Count)
                        {
                            erp.SelectedResult = erp.SearchResults[i];
                            if (idx.Length > 1 && int.TryParse(idx[1], out int e)
                                && e >= 0 && e < erp.Elements.Count)
                                erp.SelectedElement = erp.Elements[e];
                            erp.AttachCommand.Execute(null);
                        }
                        else ctx.Log($"[erp] no result at index (have {erp.SearchResults.Count})");
                        break;
                    }
                    case "detach":  erp.DetachCommand.Execute(null); break;
                    case "newelem":
                    {
                        // Creates on the attached record when one is linked, else on the selected result.
                        string name = parts.ElementAtOrDefault(1)?.Trim() ?? "";
                        if (erp.ShowAttachmentElementCreate)
                        {
                            if (name.Length > 0) erp.AttachmentElementName = name;
                            erp.CreateAttachmentElementCommand.Execute(null);
                        }
                        else
                        {
                            if (name.Length > 0) erp.NewElementName = name;
                            erp.CreateElementCommand.Execute(null);
                        }
                        break;
                    }
                    case "sendslice": erp.SendSliceCommand.Execute(null); break;
                    case "reattach":  erp.ReattachToProjectCommand.Execute(null); break;
                    case "pricing":
                        if (erp.PricingConfig is { } cfg)
                        {
                            ctx.Log($"[erp] {erp.PricingSummary}");
                            foreach (var m in cfg.Materials)
                                ctx.Log($"[erp]   {m.Name}: ${m.CostPerKg:0.00}/kg (density {m.DensityGmCc:0.###})");
                            foreach (var d in cfg.QuantityDiscounts)
                                ctx.Log($"[erp]   {d.MinQuantity}+ units → {d.Rate:P0} off");
                        }
                        else ctx.Log("[erp] no pricing config cached — fetching…");
                        _ = erp.RefreshPricingAsync();
                        break;
                    case "quote":
                    {
                        var vp = ctx.Main.Viewport;
                        if (vp.StatsTimeSeconds <= 0 && vp.StatsWeightKg <= 0)
                        {
                            ctx.Log("[erp] no slice stats — slice a model first.");
                            break;
                        }
                        var qargs = (parts.ElementAtOrDefault(1) ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        int qty = qargs.Length > 0 && int.TryParse(qargs[0], out int q) ? q : 1;
                        bool finishing = qargs.Contains("finishing", StringComparer.OrdinalIgnoreCase);
                        var req = new MassiveSlicer.App.Erp.ErpQuoteRequest(
                            PrintTimeSec: vp.StatsTimeSeconds > 0 ? vp.StatsTimeSeconds : null,
                            WeightKg:     vp.StatsWeightKg > 0 ? vp.StatsWeightKg : null,
                            Material:     ctx.Main.RightPanel.Additive.SelectedPreset?.Name,
                            Quantity:     qty,
                            Finishing:    finishing);
                        ctx.Log($"[erp] requesting quote (qty {qty}{(finishing ? ", finishing" : "")})…");
                        _ = erp.QuoteAsync(req).ContinueWith(t =>
                        {
                            var r = t.Result;
                            if (r.Ok)
                            {
                                var c = r.Value!;
                                ctx.Log($"[erp] quote: machine ${c.MachineCost:0.00} + material ${c.MaterialCost:0.00}"
                                    + (c.QuantityDiscount is { } qd and not 0.0 ? $" − discount ${qd:0.00}" : "")
                                    + (c.Markup is { } mk and not 0.0 ? $" + markup ${mk:0.00}" : "")
                                    + $" → CLIENT PRICE ${c.ClientPrice:0.00} (v{c.PricingVersion})");
                            }
                            else ctx.Log($"[erp] quote failed: {r.Error!.Message}");
                        }, TaskScheduler.Default);
                        break;
                    }
                    default:
                        ctx.Log($"[erp] state={erp.ConnectionState} status='{erp.Status}' results={erp.SearchResults.Count} " +
                                $"elements={erp.Elements.Count} attached='{erp.ToggleLabel}'");
                        break;
                }
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "smb",
            Description = "Robot SMB: smb host <ip> | share <s> | folder <f> | user <u> | pass <p> | test | send | status",
            Execute = (ctx, args) =>
            {
                var smb = ctx.Main.Viewport.RobotSmb;
                var parts = args.Trim().Split(' ', 2);
                switch (parts[0].ToLowerInvariant())
                {
                    case "host":   smb.Host     = parts.ElementAtOrDefault(1)?.Trim() ?? ""; ctx.Log($"[smb] host = {smb.Host}"); break;
                    case "share":  smb.Share    = parts.ElementAtOrDefault(1)?.Trim() ?? "d"; ctx.Log($"[smb] share = {smb.Share}"); break;
                    case "folder": smb.Folder   = parts.ElementAtOrDefault(1)?.Trim() ?? ""; ctx.Log($"[smb] folder = {smb.Folder}"); break;
                    case "user":   smb.Username = parts.ElementAtOrDefault(1)?.Trim() ?? ""; ctx.Log($"[smb] user = {smb.Username}"); break;
                    case "pass":   smb.Password = parts.ElementAtOrDefault(1) ?? ""; ctx.Log("[smb] password set"); break;
                    case "test":   smb.TestCommand.Execute(null); break;
                    case "send":   ctx.Main.Viewport.SendToRobotCommand.Execute(null); break;
                    default:
                        ctx.Log($"[smb] cell='{smb.CellName}' host='{smb.Host}' share='{smb.Share}' folder='{smb.Folder}' " +
                                $"user='{smb.Username}' configured={smb.IsConfigured} status='{smb.Status}'");
                        break;
                }
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "addset",
            Description = "Debug: get/set an AdditiveSettings property by name",
            Usage = "addset <property> [value]",
            Execute = (ctx, args) =>
            {
                var parts = args.Trim().Split(' ', 2);
                var prop = typeof(ViewModels.AdditiveSettingsViewModel).GetProperty(parts[0]);
                if (prop is null) { ctx.LogError($"[addset] no property '{parts[0]}'"); return; }
                var add = ctx.Main.RightPanel.Additive;
                if (parts.Length > 1 && prop.CanWrite)
                {
                    object value = prop.PropertyType switch
                    {
                        var t when t == typeof(double) => double.Parse(parts[1]),
                        var t when t == typeof(float)  => float.Parse(parts[1]),
                        var t when t == typeof(int)    => int.Parse(parts[1]),
                        var t when t == typeof(bool)   => bool.Parse(parts[1]),
                        _                              => parts[1],
                    };
                    prop.SetValue(add, value);
                }
                ctx.Log($"[addset] {parts[0]} = {prop.GetValue(add)}");
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "tpdump",
            Description = "Debug: dump the active toolpath moves to a CSV file",
            Usage = "tpdump <path.csv>",
            Execute = (ctx, args) =>
            {
                var tp = ctx.Main.Viewport.ActiveScrubToolpath;
                if (tp is null) { ctx.LogError("[tpdump] no active toolpath"); return; }
                string path = string.IsNullOrWhiteSpace(args)
                    ? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "toolpath-dump.csv")
                    : args.Trim();
                var sb = new System.Text.StringBuilder(
                    "layer,z,kind,fx,fy,fz,tx,ty,tz,lightning,hscale,nx,ny,nz\n");
                for (int li = 0; li < tp.Layers.Count; li++)
                {
                    var lyr = tp.Layers[li];
                    foreach (var m in lyr.Moves)
                    {
                        var n = m.Normal.LengthSquared() > 0.01f ? m.Normal : lyr.PlaneNormal;
                        sb.Append(li).Append(',').Append(lyr.Z.ToString("0.###")).Append(',')
                          .Append(m.Kind).Append(',')
                          .Append(m.From.X.ToString("0.###")).Append(',')
                          .Append(m.From.Y.ToString("0.###")).Append(',')
                          .Append(m.From.Z.ToString("0.###")).Append(',')
                          .Append(m.To.X.ToString("0.###")).Append(',')
                          .Append(m.To.Y.ToString("0.###")).Append(',')
                          .Append(m.To.Z.ToString("0.###")).Append(',')
                          .Append(m.IsLightning ? 1 : 0).Append(',')
                          .Append(m.HeightScale.ToString("0.###")).Append(',')
                          .Append(n.X.ToString("0.####")).Append(',')
                          .Append(n.Y.ToString("0.####")).Append(',')
                          .Append(n.Z.ToString("0.####")).Append('\n');
                    }
                }
                System.IO.File.WriteAllText(path, sb.ToString());
                ctx.Log($"[tpdump] {tp.Layers.Count} layer(s) → {path}");
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "effector",
            Description = "Toggle live effector point 1-3, or list active positions",
            Usage = "effector <1|2|3|list>",
            Execute = (ctx, args) =>
            {
                var arg = args.Trim();
                if (arg is "1" or "2" or "3")
                {
                    ctx.Main.Viewport.ToggleEffectorPointCommand.Execute(arg);
                    return;
                }
                var pts = ctx.Main.Viewport.GetActiveEffectorPositions();
                ctx.Log($"[effector] {pts.Count} active: " +
                        string.Join("  ", pts.Select(p => $"({p.X:0},{p.Y:0},{p.Z:0})")));
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "viewset",
            Description = "Debug: get/set a ViewportViewModel property by name",
            Usage = "viewset <property> [value]",
            Execute = (ctx, args) =>
            {
                var parts = args.Trim().Split(' ', 2);
                var prop = typeof(ViewModels.ViewportViewModel).GetProperty(parts[0]);
                if (prop is null) { ctx.LogError($"[viewset] no property '{parts[0]}'"); return; }
                var vp = ctx.Main.Viewport;
                if (parts.Length > 1 && prop.CanWrite)
                {
                    object value = prop.PropertyType switch
                    {
                        var t when t == typeof(double) => double.Parse(parts[1]),
                        var t when t == typeof(float)  => float.Parse(parts[1]),
                        var t when t == typeof(int)    => int.Parse(parts[1]),
                        var t when t == typeof(bool)   => bool.Parse(parts[1]),
                        _                              => parts[1],
                    };
                    prop.SetValue(vp, value);
                }
                ctx.Log($"[viewset] {parts[0]} = {prop.GetValue(vp)}");
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "tpopt",
            Description = "Optimize the active toolpath: greedy travel-minimizing path order + extrude bridges between paths within 3x bead width (same as the Optimize Toolpath button).",
            Usage = "tpopt",
            Execute = (ctx, _) =>
            {
                if (ctx.Main.Viewport.ActiveScrubToolpath is not { } tpo)
                {
                    ctx.LogError("[tpopt] no active toolpath — slice first");
                    return;
                }
                var stats = Core.Slicing.ToolpathOptimizer.Optimize(
                    tpo, (float)ctx.Main.RightPanel.Additive.BeadWidth);
                ctx.Main.RightPanel.Additive.OptimizeToolpathSummary = stats.ToString();
                ctx.Main.Viewport.RequestActiveToolpathReupload?.Invoke();
                ctx.Log($"[tpopt] {stats}");
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "tpfix",
            Description = "Surgical toolpath repair: build continuous support shelves under every floating run (full-length lines stepping back to the wall at 30 deg/layer). Re-run after any reslice.",
            Usage = "tpfix supports",
            Execute = (ctx, args) =>
            {
                var tp = ctx.Main.Viewport.ActiveScrubToolpath;
                if (tp is null) { ctx.LogError("[tpfix] no active toolpath"); return; }
                var add = ctx.Main.RightPanel.Additive;
                float bead = (float)add.BeadWidth;
                float layerH = (float)add.LayerHeight;
                float thr = bead * 0.75f;
                float maxReach = bead * 4f;
                float maxStep = MathF.Min(layerH * MathF.Tan(30f * MathF.PI / 180f), bead * 0.5f);

                var layerSegs = new List<List<(System.Numerics.Vector2 A, System.Numerics.Vector2 B)>>(tp.Layers.Count);
                foreach (var layer in tp.Layers)
                {
                    var list = new List<(System.Numerics.Vector2, System.Numerics.Vector2)>();
                    foreach (var mv in layer.Moves)
                        if (mv.Kind == Core.Models.MoveKind.Extrude)
                            list.Add((new System.Numerics.Vector2(mv.From.X, mv.From.Y),
                                      new System.Numerics.Vector2(mv.To.X, mv.To.Y)));
                    layerSegs.Add(list);
                }
                static float DistToSeg(System.Numerics.Vector2 p,
                    (System.Numerics.Vector2 A, System.Numerics.Vector2 B) s)
                {
                    var ab = s.B - s.A;
                    float l2 = ab.LengthSquared();
                    if (l2 < 1e-12f) return System.Numerics.Vector2.Distance(p, s.A);
                    float t = Math.Clamp(System.Numerics.Vector2.Dot(p - s.A, ab) / l2, 0f, 1f);
                    return System.Numerics.Vector2.Distance(p, s.A + ab * t);
                }
                float NearestBelow(int li, System.Numerics.Vector2 p, out System.Numerics.Vector2 q)
                {
                    q = p;
                    float best = float.MaxValue;
                    foreach (var seg in layerSegs[li - 1])
                    {
                        float d = DistToSeg(p, seg);
                        if (d < best)
                        {
                            best = d;
                            var ab = seg.B - seg.A;
                            float l2 = ab.LengthSquared();
                            float t = l2 < 1e-12f ? 0f
                                : Math.Clamp(System.Numerics.Vector2.Dot(p - seg.A, ab) / l2, 0f, 1f);
                            q = seg.A + ab * t;
                        }
                    }
                    return best;
                }

                // 1. Floating RUNS: consecutive floating extrudes in a layer's walk
                //    (small supported interruptions of up to 2 moves stay in the run).
                var runs = new List<(int Layer, List<System.Numerics.Vector2> Pts, List<float> G, List<System.Numerics.Vector2> Dir)>();
                for (int li = 1; li < tp.Layers.Count; li++)
                {
                    if (layerSegs[li - 1].Count == 0) continue;
                    List<System.Numerics.Vector2>? pts = null;
                    List<float>? gs = null;
                    List<System.Numerics.Vector2>? dirs = null;
                    int miss = 0;
                    void Flush()
                    {
                        if (pts is { Count: >= 2 }) runs.Add((li, pts, gs!, dirs!));
                        pts = null; gs = null; dirs = null; miss = 0;
                    }
                    foreach (var mv in tp.Layers[li].Moves)
                    {
                        if (mv.Kind != Core.Models.MoveKind.Extrude) { Flush(); continue; }
                        var mid = new System.Numerics.Vector2(
                            (mv.From.X + mv.To.X) * 0.5f, (mv.From.Y + mv.To.Y) * 0.5f);
                        float g = NearestBelow(li, mid, out var q);
                        bool floating = g > thr && g <= maxReach;
                        if (floating)
                        {
                            var d = mid - q;
                            float dl = d.Length();
                            var dir = dl > 1e-3f ? d / dl : System.Numerics.Vector2.Zero;
                            if (pts is null) { pts = []; gs = []; dirs = []; }
                            if (pts.Count == 0)
                            {
                                pts.Add(new System.Numerics.Vector2(mv.From.X, mv.From.Y));
                                gs!.Add(g); dirs!.Add(dir);
                            }
                            pts.Add(new System.Numerics.Vector2(mv.To.X, mv.To.Y));
                            gs!.Add(g); dirs!.Add(dir);
                            miss = 0;
                        }
                        else if (pts is not null && ++miss > 2) Flush();
                    }
                    Flush();
                }
                if (runs.Count == 0) { ctx.Log("[tpfix] no floating runs found — nothing to do"); return; }

                // 2. Shelves: reprint each floating run on the layers below,
                //    stepping every vertex toward the wall by <= maxStep per layer
                //    with the wall re-measured at EVERY layer (angled slicing moves
                //    it in world XY). Vertices that land (gap <= thr) leave the
                //    descent; vertices whose gap stops shrinking for 3 layers are
                //    chasing a wall that leans away faster than 30 deg and are
                //    abandoned. Runs split into independent sub-runs as vertices
                //    drop out, so material only goes where it still helps.
                int shelves = 0, abandoned = 0; float lenInjected = 0f;
                foreach (var (lyr, pts, _, _) in runs)
                {
                    var seg0 = new List<(System.Numerics.Vector2 P, float LastG, int Stall)>(pts.Count);
                    foreach (var p2 in pts) seg0.Add((p2, float.MaxValue, 0));
                    var segsAlive = new List<List<(System.Numerics.Vector2 P, float LastG, int Stall)>> { seg0 };
                    for (int level = 0, J = lyr - 1; level < 40 && J >= 1 && segsAlive.Count > 0; level++, J--)
                    {
                        var layer = tp.Layers[J];
                        var nextSegs = new List<List<(System.Numerics.Vector2, float, int)>>();
                        foreach (var seg in segsAlive)
                        {
                            int n = seg.Count;
                            var g = new float[n];
                            var q = new System.Numerics.Vector2[n];
                            for (int vi = 0; vi < n; vi++)
                                g[vi] = NearestBelow(J, seg[vi].P, out q[vi]);

                            // Print this seg at layer J: wall anchor, along, anchor, back.
                            var line = new List<System.Numerics.Vector2>(n + 2) { q[0] };
                            for (int vi = 0; vi < n; vi++) line.Add(seg[vi].P);
                            line.Add(q[n - 1]);
                            int bi = -1; float bd = float.MaxValue; System.Numerics.Vector3 vAt = default;
                            for (int i = 0; i < layer.Moves.Count; i++)
                            {
                                var mv = layer.Moves[i];
                                if (mv.Kind != Core.Models.MoveKind.Extrude) continue;
                                float d = System.Numerics.Vector2.Distance(
                                    new System.Numerics.Vector2(mv.To.X, mv.To.Y), line[0]);
                                if (d < bd) { bd = d; bi = i; vAt = mv.To; }
                            }
                            if (bi < 0 || bd > bead * 4f) { abandoned += n; continue; }
                            var normal = layer.Moves[bi].Normal;
                            float ZAt(System.Numerics.Vector2 p2) =>
                                MathF.Abs(normal.Z) > 0.3f
                                    ? vAt.Z - (normal.X * (p2.X - vAt.X) + normal.Y * (p2.Y - vAt.Y)) / normal.Z
                                    : vAt.Z;
                            var detour = new List<Core.Models.ToolpathMove>();
                            var pos = vAt;
                            void Go(System.Numerics.Vector2 p2)
                            {
                                var p3 = new System.Numerics.Vector3(p2.X, p2.Y, ZAt(p2));
                                if (System.Numerics.Vector3.DistanceSquared(pos, p3) < 1e-6f) return;
                                detour.Add(new Core.Models.ToolpathMove(pos, p3, Core.Models.MoveKind.Extrude)
                                    { IsLightning = true, Normal = normal });
                                lenInjected += System.Numerics.Vector3.Distance(pos, p3);
                                pos = p3;
                            }
                            foreach (var p2 in line) Go(p2);
                            for (int vi = line.Count - 2; vi >= 0; vi--) Go(line[vi]);
                            Go(new System.Numerics.Vector2(vAt.X, vAt.Y));
                            layer.Moves.InsertRange(bi + 1, detour);
                            for (int vi = 1; vi < line.Count; vi++)
                                layerSegs[J].Add((line[vi - 1], line[vi]));
                            shelves++;

                            // Step survivors toward the wall; split on dropouts.
                            List<(System.Numerics.Vector2, float, int)>? open = null;
                            void Close() { if (open is { Count: >= 2 }) nextSegs.Add(open); open = null; }
                            for (int vi = 0; vi < n; vi++)
                            {
                                if (g[vi] <= thr) { Close(); continue; }               // landed
                                int stall = g[vi] > seg[vi].LastG - maxStep * 0.4f
                                    ? seg[vi].Stall + 1 : 0;
                                if (stall >= 3) { abandoned++; Close(); continue; }    // wall leans away
                                var d = seg[vi].P - q[vi];
                                float dl = d.Length();
                                var stepped = dl > 1e-3f
                                    ? seg[vi].P - d / dl * MathF.Min(maxStep, g[vi])
                                    : seg[vi].P;
                                open ??= [];
                                open.Add((stepped, g[vi], stall));
                            }
                            Close();
                        }
                        segsAlive = nextSegs
                            .Select(sg => sg.Select(t => ((System.Numerics.Vector2)t.Item1, t.Item2, t.Item3)).ToList())
                            .ToList();
                    }
                }
                ctx.Main.Viewport.RequestActiveToolpathReupload?.Invoke();
                ctx.Log($"[tpfix] {runs.Count} floating run(s) → {shelves} support shelf line(s), "
                    + $"{lenInjected / 1000f:0.0} m extruded"
                    + (abandoned > 0 ? $", {abandoned} vertex chase(s) abandoned (wall leans past 30°)" : "")
                    + ". Applied to the CURRENT toolpath — re-run after any reslice.");
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "robotset",
            Description = "Debug: get/set a RobotPanelViewModel property by name (A1..A6, E1, ...)",
            Usage = "robotset <property> [value]",
            Execute = (ctx, args) =>
            {
                var parts = args.Trim().Split(' ', 2);
                var prop = typeof(ViewModels.RobotPanelViewModel).GetProperty(parts[0]);
                if (prop is null) { ctx.LogError($"[robotset] no property '{parts[0]}'"); return; }
                if (ctx.Main.Viewport.Robot is not { } robot)
                {
                    ctx.LogError("[robotset] no robot panel");
                    return;
                }
                if (parts.Length > 1 && prop.CanWrite)
                {
                    object value = prop.PropertyType switch
                    {
                        var t when t == typeof(double) => double.Parse(parts[1]),
                        var t when t == typeof(float)  => float.Parse(parts[1]),
                        var t when t == typeof(int)    => int.Parse(parts[1]),
                        var t when t == typeof(bool)   => bool.Parse(parts[1]),
                        _                              => parts[1],
                    };
                    prop.SetValue(robot, value);
                }
                ctx.Log($"[robotset] {parts[0]} = {prop.GetValue(robot)}");
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "simkey",
            Description = "Sim-timeline camera keyframes: add at current %, clear, or list",
            Usage = "simkey <add|clear|list>",
            Execute = (ctx, args) =>
            {
                var vp = ctx.Main.Viewport;
                switch (args.Trim().ToLowerInvariant())
                {
                    case "add":
                        vp.AddSimCameraKeyframeCommand.Execute(null);
                        ctx.Log($"[simkey] keyframe at {vp.SimTimelinePercent:0.#}% — now {vp.SimCameraKeyframeMarkers.Count} keyframe(s)");
                        break;
                    case "clear":
                        vp.ClearSimCameraKeyframesCommand.Execute(null);
                        ctx.Log("[simkey] cleared");
                        break;
                    default:
                        ctx.Log($"[simkey] {vp.SimCameraKeyframeMarkers.Count} keyframe(s): "
                            + string.Join(", ", vp.SimCameraKeyframeMarkers.Select(m => $"{m * 100:0.#}%")));
                        break;
                }
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "paint",
            Description = "Toolpath paint marks: bridge/remove dabs, list, clear; support eval of selection",
            Usage = "paint <bridge|remove> <x> <y> <z> <radius> | paint list | paint clear | paint support | paint support layer",
            Execute = (ctx, args) =>
            {
                var add = ctx.Main.RightPanel.Additive;
                var parts = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                switch (parts.FirstOrDefault())
                {
                    case "bridge" or "remove" when parts.Length >= 5:
                        add.PaintMarks.Add(new MassiveSlicer.Core.Models.PaintMark(
                            new System.Numerics.Vector3(
                                float.Parse(parts[1]), float.Parse(parts[2]), float.Parse(parts[3])),
                            float.Parse(parts[4]),
                            parts[0] == "bridge"
                                ? MassiveSlicer.Core.Models.PaintMarkKind.Bridge
                                : MassiveSlicer.Core.Models.PaintMarkKind.Remove));
                        add.BumpPaintStamp();
                        ctx.Log($"[paint] {parts[0]} mark added ({add.PaintMarks.Count} total)");
                        break;
                    case "clear":
                        add.ClearPaintMarksCommand.Execute(null);
                        ctx.Log("[paint] cleared");
                        break;
                    case "support":
                        // paint support        → current edit selection
                        // paint support layer  → every island on the current scrub layer
                        EvalPaintSupport(ctx, wholeLayer: parts.Length > 1
                            && parts[1].Equals("layer", StringComparison.OrdinalIgnoreCase));
                        break;
                    default:
                        foreach (var m in add.PaintMarks)
                            ctx.Log($"[paint] {m.Kind} ({m.Center.X:0.#},{m.Center.Y:0.#},{m.Center.Z:0.#}) r={m.Radius:0.#}");
                        ctx.Log($"[paint] {add.PaintMarks.Count} mark(s)");
                        break;
                }
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "support",
            Aliases = ["struct"],
            Description = "Structural Supports: list, select/rename, move/rotate/resize, delete "
                + "— drives the same fields as the panel and the viewport gizmo",
            Usage = "support add <x> <y> [layer] | "
                + "support list | support select <name|#> | support rename <name> | "
                + "support move <x> <y> | support nudge <dx> <dy> | support rotate <deg> | "
                + "support size <width> [depth] | support layers <up> <down> | "
                + "support shape <rect|circle> | support enable <on|off> | support neck | "
                + "support where | support trace | support delete",
            Execute = (ctx, args) =>
            {
                var add = ctx.Main.RightPanel.Additive;
                var vp = ctx.Main.Viewport;
                var parts = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var specs = add.StructuralSupports;

                bool HasSelection()
                {
                    if (add.SelectedSupportIndex >= 0 && add.SelectedSupportIndex < specs.Count)
                        return true;
                    ctx.LogError("[support] no support selected — 'support list' then 'support select <name|#>'");
                    return false;
                }

                void LogSelected(string what)
                {
                    int i = add.SelectedSupportIndex;
                    var s = specs[i];
                    ctx.Log($"[support] {add.SupportNameAt(i)} {what} → {s.Shape} centre "
                        + $"({s.CenterX:0.#}, {s.CenterY:0.#}) · {s.WidthMm:0}×{s.DepthMm:0} mm · "
                        + $"{s.RotationDeg:0}° · anchor ({s.AnchorX:0.#}, {s.AnchorY:0.#}) L{s.AnchorLayer} · "
                        + $"layers +{s.LayersUp}/-{s.LayersDown} · {(s.Enabled ? "enabled" : "disabled")}");
                }

                switch (parts.FirstOrDefault()?.ToLowerInvariant())
                {
                    case "select" when parts.Length >= 2:
                    {
                        // Accept a name ("Support 2", or just "2"/"support2") or a 1-based index.
                        string want = string.Join(' ', parts.Skip(1));
                        int found = -1;
                        for (int i = 0; i < specs.Count; i++)
                            if (add.SupportNameAt(i).Replace(" ", "")
                                    .Equals(want.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
                            { found = i; break; }
                        if (found < 0 && int.TryParse(want, out int oneBased)
                            && oneBased >= 1 && oneBased <= specs.Count)
                            found = oneBased - 1;
                        if (found < 0) { ctx.LogError($"[support] no support matching '{want}'"); return; }
                        add.SelectedSupportIndex = found;
                        LogSelected("selected");
                        break;
                    }
                    case "rename" when parts.Length >= 2:
                        if (!HasSelection()) return;
                        add.SelectedSupportName = string.Join(' ', parts.Skip(1));
                        ctx.Log($"[support] renamed to {add.SelectedSupportName}");
                        break;
                    case "move" when parts.Length >= 3:
                        if (!HasSelection()) return;
                        add.SupportCenterX = double.Parse(parts[1]);
                        add.SupportCenterY = double.Parse(parts[2]);
                        LogSelected("moved");
                        break;
                    case "nudge" when parts.Length >= 3:
                        if (!HasSelection()) return;
                        add.SupportCenterX += double.Parse(parts[1]);
                        add.SupportCenterY += double.Parse(parts[2]);
                        LogSelected("nudged");
                        break;
                    case "rotate" when parts.Length >= 2:
                        if (!HasSelection()) return;
                        add.SupportRotationDeg = double.Parse(parts[1]);
                        LogSelected("rotated");
                        break;
                    case "size" when parts.Length >= 2:
                        if (!HasSelection()) return;
                        add.SupportWidthMm = double.Parse(parts[1]);
                        if (parts.Length >= 3) add.SupportDepthMm = double.Parse(parts[2]);
                        LogSelected("resized");
                        break;
                    case "layers" when parts.Length >= 3:
                        if (!HasSelection()) return;
                        add.SupportLayersUp = int.Parse(parts[1]);
                        add.SupportLayersDown = int.Parse(parts[2]);
                        LogSelected("layer range set");
                        break;
                    case "shape" when parts.Length >= 2:
                        if (!HasSelection()) return;
                        add.SupportShape = parts[1].StartsWith("c", StringComparison.OrdinalIgnoreCase)
                            ? "Circle" : "Rectangle";
                        LogSelected($"shape={add.SupportShape}");
                        break;
                    case "enable" when parts.Length >= 2:
                        if (!HasSelection()) return;
                        add.SupportEnabled = parts[1] is "on" or "true" or "1" or "yes";
                        LogSelected("toggled");
                        break;
                    case "add" when parts.Length >= 3:
                    {
                        // Mirrors AddStructuralSupportFromSelection's geometry choices so
                        // verifying through this path actually means something: snap the
                        // anchor to the nearest bead, put the pocket one bead-pair inboard.
                        if (vp.ActiveScrubToolpath is not { Layers.Count: > 0 } atp)
                        {
                            ctx.LogError("[support add] no active toolpath — slice, then select "
                                + "the toolpath so a scrub is armed");
                            return;
                        }
                        float wx = float.Parse(parts[1]), wy = float.Parse(parts[2]);
                        int layerIdx = parts.Length >= 4
                            ? Math.Clamp(int.Parse(parts[3]) - 1, 0, atp.Layers.Count - 1)
                            : Math.Clamp(vp.CurrentScrubLayerIndex, 0, atp.Layers.Count - 1);

                        var layer = atp.Layers[layerIdx];
                        float bestD2 = float.MaxValue;
                        System.Numerics.Vector3 mid = default, dirFrom = default, dirTo = default;
                        foreach (var mv in layer.Moves)
                        {
                            if (mv.Kind != MoveKind.Extrude) continue;
                            var m = (mv.From + mv.To) * 0.5f;
                            float d2 = (m.X - wx) * (m.X - wx) + (m.Y - wy) * (m.Y - wy);
                            if (d2 >= bestD2) continue;
                            bestD2 = d2; mid = m; dirFrom = mv.From; dirTo = mv.To;
                        }
                        if (bestD2 == float.MaxValue)
                        {
                            ctx.LogError($"[support add] no extrude moves on layer {layerIdx + 1}");
                            return;
                        }

                        var dir = new System.Numerics.Vector2(dirTo.X - dirFrom.X, dirTo.Y - dirFrom.Y);
                        if (dir.LengthSquared() < 1e-6f) dir = new(1, 0);
                        dir = System.Numerics.Vector2.Normalize(dir);
                        var left = new System.Numerics.Vector2(-dir.Y, dir.X);
                        const float depth = 42f;
                        var centre = new System.Numerics.Vector2(mid.X, mid.Y)
                            + left * (depth * 0.5f + (float)add.BeadWidth * 2f);

                        add.AddStructuralSupport(new StructuralSupportSpec
                        {
                            AnchorX = mid.X, AnchorY = mid.Y, AnchorLayer = layerIdx,
                            CenterX = centre.X, CenterY = centre.Y,
                            WidthMm = 92f, DepthMm = depth,
                            LayersUp = 9999, LayersDown = 0,
                        });
                        LogSelected($"added (snapped {MathF.Sqrt(bestD2):0.#} mm to nearest bead "
                            + $"on L{layerIdx + 1})");
                        ctx.Log("[support add] press Update Slice (or run `slice`) to bake it.");
                        break;
                    }
                    case "neck":
                        EvalSupportNeck(ctx);
                        break;
                    case "where":
                        foreach (var line in (vp.DescribeSupportPick?.Invoke()
                                ?? "[support where] viewport not wired").Split('\n'))
                            ctx.Log(line);
                        break;
                    case "trace":
                        if (!HasSelection()) return;
                        EvalSupportTrace(ctx, specs[add.SelectedSupportIndex],
                            add.SupportNameAt(add.SelectedSupportIndex));
                        break;
                    case "delete" or "remove":
                    {
                        if (!HasSelection()) return;
                        string gone = add.SupportNameAt(add.SelectedSupportIndex);
                        // Same path as the panel's trash icon: repairs card links + re-slices.
                        vp.DeleteSelectedStructuralSupportCommand.Execute(null);
                        ctx.Log($"[support] deleted {gone} ({specs.Count} left)");
                        break;
                    }
                    default:
                        if (specs.Count == 0)
                        {
                            ctx.Log("[support] no structural supports "
                                + "(edit mode → click a bead → type 'Structural Support')");
                            return;
                        }
                        for (int i = 0; i < specs.Count; i++)
                        {
                            var s = specs[i];
                            ctx.Log($"[support] {(i == add.SelectedSupportIndex ? "*" : " ")}"
                                + $"{i + 1}. {add.SupportNameAt(i)} · {s.Shape} "
                                + $"{s.WidthMm:0}×{s.DepthMm:0} mm @ ({s.CenterX:0.#}, {s.CenterY:0.#}) "
                                + $"{s.RotationDeg:0}° · anchor ({s.AnchorX:0.#}, {s.AnchorY:0.#}) "
                                + $"L{s.AnchorLayer} · layers +{s.LayersUp}/-{s.LayersDown}"
                                + $"{(s.Enabled ? "" : " · DISABLED")}");
                        }
                        ctx.Log($"[support] {specs.Count} support(s) · * = live (panel + gizmo target)");
                        break;
                }
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "edit",
            Description = "Toolpath edit mode (the pencil): open/close it, and toggle the 2D "
                + "slice plane viewer — makes the edit-mode-only UI reachable without clicking",
            Usage = "edit | edit on | edit off | edit 2d on | edit 2d off",
            Execute = (ctx, args) =>
            {
                var vp = ctx.Main.Viewport;
                var parts = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                bool On(string s) => s is "on" or "true" or "1" or "yes";

                if (parts.Length >= 2 && parts[0].Equals("2d", StringComparison.OrdinalIgnoreCase))
                {
                    if (!vp.IsPaintEditOpen)
                    {
                        ctx.LogError("[edit] open edit mode first ('edit on') — "
                            + "the 2D viewer only exists inside it");
                        return;
                    }
                    vp.IsSlicePlaneViewerActive = On(parts[1].ToLowerInvariant());
                    ctx.Log($"[edit] 2D slice plane viewer {(vp.IsSlicePlaneViewerActive ? "on" : "off")}");
                    return;
                }

                if (parts.Length >= 1)
                {
                    // Edit mode only exists in Preview — take the user there rather than
                    // silently doing nothing (the pencil button is Preview-only too).
                    bool want = On(parts[0].ToLowerInvariant());
                    if (want && vp.ViewMode != "Preview")
                    {
                        vp.ViewMode = "Preview";
                        ctx.Log("[edit] switched to Preview (edit mode is Preview-only)");
                    }
                    vp.IsPaintEditOpen = want;
                }

                ctx.Log($"[edit] edit mode {(vp.IsPaintEditOpen ? "OPEN" : "closed")} · "
                    + $"2D viewer {(vp.IsSlicePlaneViewerActive ? "on" : "off")} · "
                    + $"view={vp.ViewMode} · granularity={vp.PaintSelectGranularity}");
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "viewmode",
            Description = "Debug: set the view mode (Body/Toolpath/Speed/RPM/Preview)",
            Execute = (ctx, args) =>
            {
                var m = args.Trim();
                if (m.Length == 0) { ctx.Log($"[viewmode] {ctx.Main.Viewport.ViewMode}"); return; }
                ctx.Main.Viewport.ViewMode = m;
                ctx.Log($"[viewmode] set to {ctx.Main.Viewport.ViewMode}");
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "speedtest",
            Description = "Debug: toggle adaptive speed inputs and report PrintSpeedScale spread",
            Execute = (ctx, _) =>
            {
                var v = ctx.Main.Viewport;
                var add = ctx.Main.RightPanel.Additive;
                ctx.Log($"[speedtest] before: {v.GetSpeedSpread?.Invoke() ?? "n/a"} (enabled={add.LayerSpeedAdaptEnabled})");
                add.LayerSpeedAdaptEnabled = true;
                add.LayerSpeedMinMmS = Math.Abs(add.LayerSpeedMinMmS - 20.0) < 0.01 ? 21.0 : 20.0;
                ctx.Log($"[speedtest] after:  {v.GetSpeedSpread?.Invoke() ?? "n/a"} (min={add.LayerSpeedMinMmS})");
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "massivebrain",
            Aliases = ["brain"],
            Description = "MassiveBRAIN sync server: massivebrain on|off|status",
            Execute = (ctx, args) =>
            {
                var brain = ctx.Main.Viewport.MassiveBrain;
                switch (args.Trim().ToLowerInvariant())
                {
                    case "on":  brain.Enabled = true;  break;
                    case "off": brain.Enabled = false; break;
                    default:
                        ctx.Log($"[massivebrain] {(brain.Enabled ? "enabled" : "disabled")} — {brain.Status} " +
                                $"clients={brain.ClientCount} objects={brain.ObjectCount}");
                        break;
                }
            },
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
            Description = "Import a 3D model (.glb, .stl, .obj, .3mf, .stp)",
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
            Name = "cam-focus",
            Aliases = ["focus"],
            Description = "Frame the camera on the current selection (bridge-friendly alternative to `frame`, which requires a robot connection) — use after `select <name>`",
            Execute = (ctx, _) => ctx.Main.Viewport.OnFocusRequested?.Invoke(),
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "cam-frame-all",
            Description = "Frame the whole scene (bridge-friendly alternative to `frame`, which requires a robot connection)",
            Execute = (ctx, _) => ctx.Main.Viewport.OnFrameAllRequested?.Invoke(),
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "cam-preset",
            Description = "Snap the camera to a named view preset",
            Usage = "cam-preset <Top|Bottom|Left|Right|Front|Back|Iso>",
            Execute = (ctx, args) =>
            {
                string name = args.Trim();
                if (name.Length == 0) { ctx.LogError("usage: cam-preset <Top|Bottom|Left|Right|Front|Back|Iso>"); return; }
                ctx.Main.Viewport.OnViewPresetRequested?.Invoke(name);
                ctx.Log($"[cam] preset -> {name}");
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "cam-debug",
            Description = "Print the current camera state (Azimuth/Elevation/Radius/Target)",
            Execute = (ctx, _) =>
            {
                if (ctx.Main.Viewport.GetCameraState?.Invoke() is not { } cam) { ctx.LogError("[cam] no camera state available."); return; }
                ctx.Log($"[cam] Azimuth={cam.Azimuth:0.#} Elevation={cam.Elevation:0.#} Radius={cam.Radius:0.#} Target=({cam.TargetX:0.#},{cam.TargetY:0.#},{cam.TargetZ:0.#}) Ortho={cam.IsOrthographic}");
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "cam-orbit",
            Description = "Set the camera's orbit angles directly (degrees) — use `cam-debug` first to see current values",
            Usage = "cam-orbit <azimuth> <elevation>",
            Execute = (ctx, args) =>
            {
                var parts = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2 || !float.TryParse(parts[0], out var az) || !float.TryParse(parts[1], out var el))
                {
                    ctx.LogError("usage: cam-orbit <azimuth> <elevation>");
                    return;
                }
                if (ctx.Main.Viewport.GetCameraState?.Invoke() is not { } cam) { ctx.LogError("[cam] no camera state available."); return; }
                ctx.Main.Viewport.ApplyCameraState?.Invoke(cam with { Azimuth = az, Elevation = el });
                ctx.Log($"[cam] Azimuth -> {az}, Elevation -> {el}");
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "cam-zoom",
            Description = "Set the camera's orbit radius (distance from target) directly",
            Usage = "cam-zoom <radius>",
            Execute = (ctx, args) =>
            {
                if (!float.TryParse(args.Trim(), out var radius)) { ctx.LogError("usage: cam-zoom <radius>"); return; }
                if (ctx.Main.Viewport.GetCameraState?.Invoke() is not { } cam) { ctx.LogError("[cam] no camera state available."); return; }
                ctx.Main.Viewport.ApplyCameraState?.Invoke(cam with { Radius = radius });
                ctx.Log($"[cam] Radius -> {radius}");
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "cam-pan",
            Description = "Set the camera's orbit target (look-at point) directly",
            Usage = "cam-pan <x> <y> <z>",
            Execute = (ctx, args) =>
            {
                var parts = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 3
                    || !float.TryParse(parts[0], out var x)
                    || !float.TryParse(parts[1], out var y)
                    || !float.TryParse(parts[2], out var z))
                {
                    ctx.LogError("usage: cam-pan <x> <y> <z>");
                    return;
                }
                if (ctx.Main.Viewport.GetCameraState?.Invoke() is not { } cam) { ctx.LogError("[cam] no camera state available."); return; }
                ctx.Main.Viewport.ApplyCameraState?.Invoke(cam with { TargetX = x, TargetY = y, TargetZ = z });
                ctx.Log($"[cam] Target -> ({x},{y},{z})");
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "slice",
            Aliases = ["generate-slice"],
            Description = "Slice the selected mesh into toolpaths",
            Execute = (ctx, _) =>
            {
                var vp = ctx.Main.Viewport;
                // Console `select` can leave HasMeshSelected false if the outliner
                // path didn't refresh flags — recover so automation can slice.
                if (!vp.HasMeshSelected && vp.GetSelectedSceneNode?.Invoke() is { } sel
                    && vp.FindUserMeshOutlinerItem(sel) is not null)
                {
                    vp.HasMeshSelected = true;
                    vp.SliceCommand?.RaiseCanExecuteChanged();
                }
                var slice = vp.SliceCommand;
                if (slice is not null && slice.CanExecute(null))
                {
                    slice.Execute(null);
                    ctx.Log("[slice] slicing selected mesh...");
                }
                else if (vp.IsSlicing)
                {
                    ctx.LogError("Already slicing — wait for the current slice to finish.");
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

        Register(new ConsoleCommandDefinition
        {
            Name = "viewport-home",
            Aliases = ["vhome", "reset-viewport"],
            Description = "Reset viewport robot joints to the selected home preset (no real-robot move)",
            Execute = (ctx, _) => ctx.Log(ctx.Main.ApplyViewportHome()),
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
            Name = "cal-check",
            Aliases = ["calcheck", "cell-check"],
            Description = "Cell calibration check: compare where the app DRAWS the nozzle tip against where the controller says it is, both in BASE-frame mm. Jog the real TCP to a known point (e.g. the bed corner), then run this — the reported error is the cell's calibration error.",
            Execute = (ctx, _) =>
            {
                if (ctx.Main.Viewport.Robot is not { } robot) { ctx.LogError("[cal-check] no robot panel"); return; }
                if (ctx.Main.Viewport.ActiveCell is not { } cell) { ctx.LogError("[cal-check] no active cell"); return; }

                var rp     = cell.Robot.WorldPosition;
                var marker = cell.Bed.BaseMarkerWorld(rp);
                var grid   = cell.Bed.VisualGridCorner(rp);

                // Where the scene graph actually draws the tip, converted into BASE frame.
                double sceneBaseX = robot.SceneTcpX - marker.X;
                double sceneBaseY = robot.SceneTcpY - marker.Y;
                double sceneBaseZ = robot.SceneTcpZ - marker.Z;

                // Echo the cells directory and modelOffset: the single most common way to get a
                // confusing result here is running against a different copy of the cell JSON
                // (env var -> NAS -> build output, see CellPaths) than you think you are.
                ctx.Log($"[cal-check] cell '{cell.Name}'  robroot=({rp.X:F1}, {rp.Y:F1}, {rp.Z:F1})"
                      + $"  modelOffset={(cell.Robot.ModelOffset is { } mo ? $"({mo.X:F2}, {mo.Y:F2}, {mo.Z:F2})" : "none")}");
                ctx.Log($"[cal-check] cells dir: {MassiveSlicer.Core.IO.CellPaths.PreferredCellsDirectory() ?? "(unresolved)"}");
                ctx.Log($"[cal-check] BASE marker world=({marker.X:F1}, {marker.Y:F1}, {marker.Z:F1})   visual grid corner world=({grid.X:F1}, {grid.Y:F1}, {grid.Z:F1})");
                ctx.Log($"[cal-check] visualOffset={(cell.Bed.VisualOffset is { } vo ? $"({vo.X:F1}, {vo.Y:F1})" : "none")}");
                ctx.Log($"[cal-check] app-drawn tip:  world=({robot.SceneTcpX:F1}, {robot.SceneTcpY:F1}, {robot.SceneTcpZ:F1})  ->  BASE=({sceneBaseX:F1}, {sceneBaseY:F1}, {sceneBaseZ:F1})");

                if (!robot.IsConnected)
                {
                    ctx.Log("[cal-check] robot not synced — connect first to compare against the controller.");
                    return;
                }

                ctx.Log($"[cal-check] controller tip: BASE=({robot.CtlTcpX:F1}, {robot.CtlTcpY:F1}, {robot.CtlTcpZ:F1})  (tool #1, base #1)");
                ctx.Log($"[cal-check] ERROR (app − controller) = ({sceneBaseX - robot.CtlTcpX:F1}, {sceneBaseY - robot.CtlTcpY:F1}, {sceneBaseZ - robot.CtlTcpZ:F1}) mm"
                      + $"   XY magnitude {Math.Sqrt(Math.Pow(sceneBaseX - robot.CtlTcpX, 2) + Math.Pow(sceneBaseY - robot.CtlTcpY, 2)):F1} mm");
                ctx.Log("[cal-check] A non-zero error means the app's robot model is misplaced relative to the cell, "
                      + "NOT that the bed needs moving — repeat at a second, far-apart point to tell a constant frame offset from a kinematic error.");

                // Compact machine-readable sample: one line carrying joints, rail, controller
                // pose and the error, so a poller can build a multi-pose dataset from a live
                // job without stopping the robot. Classifying the error needs E1 (rail term)
                // and ABC (tool term) alongside the XYZ, hence all of it on one line.
                ctx.Log($"[cal-sample] {robot.A1:F2},{robot.A2:F2},{robot.A3:F2},{robot.A4:F2},{robot.A5:F2},{robot.A6:F2},{robot.E1:F2},"
                      + $"{robot.CtlTcpX:F1},{robot.CtlTcpY:F1},{robot.CtlTcpZ:F1},{robot.CtlTcpA:F3},{robot.CtlTcpB:F3},{robot.CtlTcpC:F3},"
                      + $"{robot.SceneTcpX:F1},{robot.SceneTcpY:F1},{robot.SceneTcpZ:F1},"
                      + $"{sceneBaseX - robot.CtlTcpX:F1},{sceneBaseY - robot.CtlTcpY:F1},{sceneBaseZ - robot.CtlTcpZ:F1}");
            },
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
            Name = "objects",
            Aliases = ["ls", "list-objects"],
            Description = "List user content objects (imports/scans/toolpaths) with mesh/pick info",
            Execute = (ctx, _) =>
            {
                foreach (var line in ctx.Main.Viewport.ListContentObjects().Split('\n'))
                    ctx.Log(line.TrimEnd('\r'));
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "outliner-tree",
            Aliases = ["tree"],
            Description = "Dump the full outliner hierarchy (real parent/child nesting + group/modifier/toolpath/visibility flags) — verify structural fixes without a screenshot",
            Execute = (ctx, _) =>
            {
                foreach (var line in ctx.Main.Viewport.DescribeOutlinerTree().Split('\n'))
                    ctx.Log(line.TrimEnd('\r'));
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "select",
            Aliases = ["sel"],
            Description = "Select a content object by name (drives the outliner selection path)",
            Usage = "select <name> [--toolpath]",
            Execute = (ctx, args) =>
            {
                if (string.IsNullOrWhiteSpace(args)) { ctx.LogError("usage: select <name>  (run `objects` to list)"); return; }
                if (args.EndsWith("--toolpath", StringComparison.OrdinalIgnoreCase))
                {
                    string name = args[..^"--toolpath".Length].Trim();
                    var item = ctx.Main.Viewport.GetUserModelItems()
                        .FirstOrDefault(m => m.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
                    var tp = item?.Children.FirstOrDefault(c => c.IsToolpath);
                    if (tp is null) { ctx.LogError($"[select] no toolpath under '{name}'"); return; }
                    ctx.Main.Viewport.ForceSelectNode?.Invoke(tp.Node);
                    ctx.Log($"[select] toolpath of \"{item!.Name}\" selected.");
                    return;
                }
                ctx.Log(ctx.Main.Viewport.SelectByName(args));
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "pick",
            Description = "Debug: simulate a viewport click at fractional coords (0-1) and trace the selection gates",
            Usage = "pick <fx> <fy>   e.g. pick 0.5 0.5 (viewport centre)",
            Execute = (ctx, args) =>
            {
                var parts = (args ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2
                    || !double.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out double fx)
                    || !double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out double fy))
                { ctx.LogError("usage: pick <fx 0-1> <fy 0-1>"); return; }
                var trace = ctx.Main.Viewport.DebugPickAtViewport?.Invoke(fx, fy) ?? "[pick] viewport hook not wired";
                foreach (var line in trace.Split('\n'))
                    ctx.Log(line.TrimEnd('\r'));
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "selection",
            Aliases = ["selected"],
            Description = "Report the renderer's current selection (what would be highlighted)",
            Execute = (ctx, _) => ctx.Log(ctx.Main.Viewport.DescribeSelection()),
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "modifier-add",
            Aliases = ["addmodifier", "add-cut"],
            Description = "Add a Cut modifier to the currently-selected model (step 5 MODIFIERS) — it becomes the new selection",
            Execute = (ctx, _) =>
            {
                var panel = ctx.Main.RightPanel.Modifiers;
                if (!panel.HasOwner) { ctx.LogError("[modifier] select a model first."); return; }
                panel.AddCutModifierCommand.Execute(null);
                var owner = ctx.Main.Viewport.SelectedModifierOwner;
                int count = owner is null ? 0 : ctx.Main.Viewport.GetModifiers(owner).Count;
                ctx.Log($"[modifier] added \"{panel.SelectedSettings?.Name ?? "?"}\" ({count} in stack).");
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "modifier-debug",
            Description = "Diagnostic: dump the modifier panel/selection chain",
            Execute = (ctx, _) =>
            {
                var panel = ctx.Main.RightPanel.Modifiers;
                ctx.Log($"[debug] RightPanel.Modifiers == Viewport.ModifiersPanel: {ReferenceEquals(panel, ctx.Main.Viewport.ModifiersPanel)}");
                ctx.Log($"[debug] HasOwner: {panel.HasOwner}");
                ctx.Log($"[debug] SelectedSettings: {panel.SelectedSettings?.Name ?? "null"}");
                ctx.Log($"[debug] IsGroupSelected: {panel.IsGroupSelected}");
                var cut = panel.SelectedSettings?.Cut;
                ctx.Log($"[debug] SelectedSettings.Cut is CutModifier: {cut is not null}");
                if (cut is not null)
                    ctx.Log($"[debug] Cut: Enabled={cut.Enabled} PreviewVisible={cut.PreviewVisible} Orientation={cut.Orientation} Offset={cut.Offset} SizeX={cut.SizeX} SizeY={cut.SizeY}");
                ctx.Log($"[debug] SelectedSettings.LayerNumber: {panel.SelectedSettings?.LayerNumber?.ToString() ?? "null"}");
                ctx.Log($"[debug] Viewport.SelectedOutlinerItem: {ctx.Main.Viewport.SelectedOutlinerItem?.Name ?? "null"}");
                ctx.Log($"[debug] Viewport.SelectedModifierOwner: {ctx.Main.Viewport.SelectedModifierOwner?.Name ?? "null"}");
                ctx.Log($"[debug] IsCutToolActive={ctx.Main.Viewport.IsCutToolActive} AdditiveMethod={ctx.Main.Viewport.AdditiveSettings?.Method}");
                ctx.Log($"[debug] XBracingEnabled={ctx.Main.Viewport.AdditiveSettings?.XBracingEnabled} XBracingShowHelper={ctx.Main.Viewport.AdditiveSettings?.XBracingShowHelper}");
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "modifier-set-offset",
            Description = "Diagnostic: set the selected modifier's Offset directly (verifies the preview reacts, without needing a mouse drag)",
            Usage = "modifier-set-offset <value>",
            Execute = (ctx, args) =>
            {
                var settings = ctx.Main.RightPanel.Modifiers.SelectedSettings;
                if (settings is null) { ctx.LogError("[modifier] nothing selected."); return; }
                if (!float.TryParse(args.Trim(), out var value)) { ctx.LogError("usage: modifier-set-offset <value>"); return; }
                settings.Offset = value;
                ctx.Log($"[modifier] {settings.Name} Offset -> {settings.Offset}");
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "modifier-set-layer",
            Description = "Diagnostic: set the selected Horizontal modifier's LayerNumber directly (snaps Offset to that real toolpath layer's Z)",
            Usage = "modifier-set-layer <1-based layer number>",
            Execute = (ctx, args) =>
            {
                var settings = ctx.Main.RightPanel.Modifiers.SelectedSettings;
                if (settings is null) { ctx.LogError("[modifier] nothing selected."); return; }
                if (!int.TryParse(args.Trim(), out var value)) { ctx.LogError("usage: modifier-set-layer <1-based layer number>"); return; }
                settings.LayerNumber = value;
                ctx.Log($"[modifier] {settings.Name} LayerNumber -> {settings.LayerNumber} (Offset now {settings.Offset})");
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "modifier-set-rotation",
            Description = "Diagnostic: set the selected Vertical modifier's RotationDegrees directly",
            Usage = "modifier-set-rotation <degrees>",
            Execute = (ctx, args) =>
            {
                var settings = ctx.Main.RightPanel.Modifiers.SelectedSettings;
                if (settings is null) { ctx.LogError("[modifier] nothing selected."); return; }
                if (!float.TryParse(args.Trim(), out var value)) { ctx.LogError("usage: modifier-set-rotation <degrees>"); return; }
                settings.IsVertical = true;
                settings.RotationDegrees = value;
                ctx.Log($"[modifier] {settings.Name} RotationDegrees -> {settings.RotationDegrees}");
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "modifier-set-infinite",
            Description = "Diagnostic: set the selected modifier's Infinite flag directly (true = unbounded plane, false = Restricted/bounded — verifies Apply without needing a checkbox click)",
            Usage = "modifier-set-infinite <true|false>",
            Execute = (ctx, args) =>
            {
                var settings = ctx.Main.RightPanel.Modifiers.SelectedSettings;
                if (settings is null) { ctx.LogError("[modifier] nothing selected."); return; }
                if (!bool.TryParse(args.Trim(), out var value)) { ctx.LogError("usage: modifier-set-infinite <true|false>"); return; }
                settings.Infinite = value;
                ctx.Log($"[modifier] {settings.Name} Infinite -> {settings.Infinite}");
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "modifier-set-size",
            Description = "Diagnostic: set the selected Restricted-mode modifier's SizeX/SizeY directly",
            Usage = "modifier-set-size <sizeX> <sizeY>",
            Execute = (ctx, args) =>
            {
                var settings = ctx.Main.RightPanel.Modifiers.SelectedSettings;
                if (settings is null) { ctx.LogError("[modifier] nothing selected."); return; }
                var parts = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2
                    || !float.TryParse(parts[0], out var sizeX)
                    || !float.TryParse(parts[1], out var sizeY))
                {
                    ctx.LogError("usage: modifier-set-size <sizeX> <sizeY>");
                    return;
                }
                settings.SizeX = sizeX;
                settings.SizeY = sizeY;
                ctx.Log($"[modifier] {settings.Name} SizeX -> {settings.SizeX}, SizeY -> {settings.SizeY}");
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "modifier-node-debug",
            Description = "Diagnostic: dump the selected modifier's plane object transform/flags (confirms it's a real, independent scene object, parented into its owner's Modifiers group)",
            Execute = (ctx, _) =>
            {
                var settings = ctx.Main.RightPanel.Modifiers.SelectedSettings;
                if (settings?.Cut is not { } cut) { ctx.LogError("[modifier] nothing selected."); return; }
                var vp = ctx.Main.Viewport;

                var gizmoNode = vp.GetModifierGizmoNode(cut);
                if (gizmoNode is null) { ctx.LogError("[modifier] no gizmo node (shouldn't happen for a live selection)."); return; }
                var gw = gizmoNode.WorldTransform;
                ctx.Log($"[debug] gizmo node parent: {(gizmoNode.Parent is { } p ? p.Name : "(none)")}");
                ctx.Log($"[debug] gizmo Selectable={gizmoNode.Selectable} PickIgnore={gizmoNode.PickIgnore} Visible={gizmoNode.Visible}");
                ctx.Log($"[debug] gizmo world pos: ({gw.Row3.X:0.##}, {gw.Row3.Y:0.##}, {gw.Row3.Z:0.##})");
                ctx.Log($"[debug] gizmo world Row0 (local X): ({gw.Row0.X:0.###}, {gw.Row0.Y:0.###}, {gw.Row0.Z:0.###})");
                ctx.Log($"[debug] gizmo world Row2 (local Z): ({gw.Row2.X:0.###}, {gw.Row2.Y:0.###}, {gw.Row2.Z:0.###})");
                var plane = cut.Orientation == MassiveSlicer.Core.Models.CutOrientation.Horizontal
                    ? System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(gw.Row2.X, gw.Row2.Y, gw.Row2.Z))
                    : System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(gw.Row0.X, gw.Row0.Y, gw.Row0.Z));
                ctx.Log($"[debug] plane normal: ({plane.X:0.###}, {plane.Y:0.###}, {plane.Z:0.###})");
                ctx.Log($"[debug] Cut fields: Orientation={cut.Orientation} Offset={cut.Offset} RotationDegrees={cut.RotationDegrees}");
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "modifier-apply",
            Aliases = ["apply-modifiers"],
            Description = "Run the selected mesh's Modifiers stack (select its Modifiers group first, or select any modifier in it)",
            Execute = (ctx, _) =>
            {
                var panel = ctx.Main.RightPanel.Modifiers;
                if (!panel.IsGroupSelected)
                {
                    ctx.LogError("[apply] select the mesh's Modifiers group first (e.g. `select Modifiers`).");
                    return;
                }
                panel.ApplyCommand.Execute(null);
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "transform-debug",
            Description = "Diagnostic: dump full-precision World/Local transform + parent name for every outliner item found anywhere matching a name",
            Usage = "transform-debug <name>",
            Execute = (ctx, args) =>
            {
                var name = args.Trim();
                if (name.Length == 0) { ctx.LogError("usage: transform-debug <name>"); return; }

                void FindAll(IEnumerable<OutlinerItemViewModel> items, List<OutlinerItemViewModel> results)
                {
                    foreach (var item in items)
                    {
                        if (item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) results.Add(item);
                        FindAll(item.Children, results);
                    }
                }

                var matches = new List<OutlinerItemViewModel>();
                FindAll(ctx.Main.Viewport.OutlinerItems, matches);
                if (matches.Count == 0) { ctx.LogError($"[debug] no outliner item named '{name}'."); return; }

                foreach (var found in matches)
                {
                    var node = found.Node;
                    var w = node.WorldTransform;
                    var l = node.LocalTransform;
                    string kind = found.IsModifiersGroup ? " [ModifiersGroup]"
                        : found.IsPiecesGroup ? " [PiecesGroup]"
                        : found.IsModifier ? " [Modifier]"
                        : found.IsToolpath ? " [Toolpath]"
                        : "";
                    ctx.Log($"[debug] \"{found.Name}\"{kind} parent=\"{node.Parent?.Name ?? "(none)"}\"");
                    ctx.Log($"[debug]   world pos=({w.Row3.X:0.######}, {w.Row3.Y:0.######}, {w.Row3.Z:0.######})");
                    ctx.Log($"[debug]   local pos=({l.Row3.X:0.######}, {l.Row3.Y:0.######}, {l.Row3.Z:0.######})");
                    ctx.Log($"[debug]   world rowX=({w.Row0.X:0.###},{w.Row0.Y:0.###},{w.Row0.Z:0.###}) rowY=({w.Row1.X:0.###},{w.Row1.Y:0.###},{w.Row1.Z:0.###}) rowZ=({w.Row2.X:0.###},{w.Row2.Y:0.###},{w.Row2.Z:0.###})");
                }
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "visible-debug",
            Description = "Diagnostic: dump Visible for every outliner item found anywhere (full recursion) matching a name, and each match's immediate children",
            Usage = "visible-debug <name>",
            Execute = (ctx, args) =>
            {
                var name = args.Trim();
                if (name.Length == 0) { ctx.LogError("usage: visible-debug <name>"); return; }

                void FindAll(IEnumerable<OutlinerItemViewModel> items, List<OutlinerItemViewModel> results)
                {
                    foreach (var item in items)
                    {
                        if (item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) results.Add(item);
                        FindAll(item.Children, results);
                    }
                }

                var matches = new List<OutlinerItemViewModel>();
                FindAll(ctx.Main.Viewport.OutlinerItems, matches);
                if (matches.Count == 0) { ctx.LogError($"[debug] no outliner item named '{name}'."); return; }

                foreach (var found in matches)
                {
                    string kind = found.IsModifiersGroup ? " [ModifiersGroup]"
                        : found.IsPiecesGroup ? " [PiecesGroup]"
                        : found.IsModifier ? " [Modifier]"
                        : found.IsToolpath ? " [Toolpath]"
                        : "";
                    ctx.Log($"[debug] \"{found.Name}\"{kind} Visible={found.Visible}");
                    foreach (var child in found.Children)
                        ctx.Log($"[debug]   child \"{child.Name}\" Visible={child.Visible}");
                }
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "visible-set-group",
            Description = "Diagnostic: set Visible on the Modifiers/Pieces group matching a name (disambiguates same-named master/group items), to verify the visibility cascade",
            Usage = "visible-set-group <name> <true|false>",
            Execute = (ctx, args) =>
            {
                var parts = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || !bool.TryParse(parts[^1], out var value))
                {
                    ctx.LogError("usage: visible-set-group <name> <true|false>");
                    return;
                }
                var name = string.Join(' ', parts[..^1]);

                void FindAll(IEnumerable<OutlinerItemViewModel> items, List<OutlinerItemViewModel> results)
                {
                    foreach (var item in items)
                    {
                        if (item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                            && (item.IsPiecesGroup || item.IsModifiersGroup))
                            results.Add(item);
                        FindAll(item.Children, results);
                    }
                }

                var matches = new List<OutlinerItemViewModel>();
                FindAll(ctx.Main.Viewport.OutlinerItems, matches);
                if (matches.Count == 0) { ctx.LogError($"[debug] no Modifiers/Pieces group named '{name}'."); return; }

                var target = matches[0];
                target.Visible = value;
                ctx.Log($"[debug] set \"{target.Name}\" [{(target.IsPiecesGroup ? "PiecesGroup" : "ModifiersGroup")}] Visible={value}");
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "visible-set",
            Description = "Diagnostic: set Visible on the first outliner item matching a name (any kind — mesh, group, toolpath), to verify visibility cascades. Add --toolpath to target the toolpath specifically when names collide with its owning mesh.",
            Usage = "visible-set <name> <true|false> [--toolpath]",
            Execute = (ctx, args) =>
            {
                bool wantToolpath = args.TrimEnd().EndsWith("--toolpath", StringComparison.OrdinalIgnoreCase);
                if (wantToolpath) args = args.TrimEnd()[..^"--toolpath".Length];

                var parts = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || !bool.TryParse(parts[^1], out var value))
                {
                    ctx.LogError("usage: visible-set <name> <true|false> [--toolpath]");
                    return;
                }
                var name = string.Join(' ', parts[..^1]);

                OutlinerItemViewModel? Find(IEnumerable<OutlinerItemViewModel> items)
                {
                    foreach (var item in items)
                    {
                        if (item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                            && (!wantToolpath || item.IsToolpath))
                            return item;
                        if (Find(item.Children) is { } found) return found;
                    }
                    return null;
                }

                var target = Find(ctx.Main.Viewport.OutlinerItems);
                if (target is null) { ctx.LogError($"[debug] no outliner item named '{name}'."); return; }
                target.Visible = value;
                ctx.Log($"[debug] set \"{target.Name}\" Visible={value}");
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "set-rotation",
            Description = "Diagnostic: set the SELECTED MODEL's rotation (A/B/C degrees) via the same typed-field path the panel uses (OnSelectionRotated) — exercises the panel-edit undo/toolpath-link path without needing a real gizmo drag",
            Usage = "set-rotation <a> <b> <c>",
            Execute = (ctx, args) =>
            {
                var parts = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 3
                    || !double.TryParse(parts[0], out var a)
                    || !double.TryParse(parts[1], out var b)
                    || !double.TryParse(parts[2], out var c))
                {
                    ctx.LogError("usage: set-rotation <a> <b> <c>");
                    return;
                }
                ctx.Main.Viewport.SelectionA = a;
                ctx.Main.Viewport.SelectionB = b;
                ctx.Main.Viewport.SelectionC = c;
                ctx.Log($"[debug] set rotation -> A={a} B={b} C={c}");
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "set-position",
            Description = "Diagnostic: set the SELECTED node's position (X/Y/Z) via the same typed-field path the panel uses (OnSelectionTranslated) — works whether a mesh or its toolpath is selected, to exercise the bidirectional move-link both directions",
            Usage = "set-position <x> <y> <z>",
            Execute = (ctx, args) =>
            {
                var parts = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 3
                    || !double.TryParse(parts[0], out var x)
                    || !double.TryParse(parts[1], out var y)
                    || !double.TryParse(parts[2], out var z))
                {
                    ctx.LogError("usage: set-position <x> <y> <z>");
                    return;
                }
                ctx.Main.Viewport.SelectionX = x;
                ctx.Main.Viewport.SelectionY = y;
                ctx.Main.Viewport.SelectionZ = z;
                ctx.Log($"[debug] set position -> X={x} Y={y} Z={z}");
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "piece-toolpath-debug",
            Description = "Diagnostic: find a piece by name anywhere in the outliner (full recursion, unlike `select --toolpath`) and dump its toolpath snapshot",
            Usage = "piece-toolpath-debug <name>",
            Execute = (ctx, args) =>
            {
                var name = args.Trim();
                if (name.Length == 0) { ctx.LogError("usage: piece-toolpath-debug <name>"); return; }

                OutlinerItemViewModel? Find(IEnumerable<OutlinerItemViewModel> items)
                {
                    foreach (var item in items)
                    {
                        if (item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return item;
                        if (Find(item.Children) is { } found) return found;
                    }
                    return null;
                }

                var piece = Find(ctx.Main.Viewport.OutlinerItems);
                if (piece is null) { ctx.LogError($"[debug] no outliner item named '{name}'."); return; }

                ctx.Log($"[debug] found \"{piece.Name}\" IsToolpath={piece.IsToolpath} children={piece.Children.Count}");
                var tpItem = piece.Children.FirstOrDefault(c => c.IsToolpath);
                if (tpItem is null) { ctx.LogError("[debug] no IsToolpath child."); return; }

                var snap = ctx.Main.Viewport.GetToolpathSnapshot?.Invoke(tpItem.Node);
                if (snap is null) { ctx.LogError("[debug] GetToolpathSnapshot returned null (not staged yet, or never registered)."); return; }
                ctx.Log($"[debug] snapshot: Smoothed.Layers={snap.Smoothed.Layers.Count} Raw.Layers={snap.Raw.Layers.Count} BeadWidth={snap.BeadWidth} LayerHeight={snap.LayerHeight}");
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "align-debug",
            Description = "Diagnostic: compare a piece's mesh world-space AABB against its toolpath's raw world-space move AABB, to check whether they actually occupy the same real-world footprint",
            Usage = "align-debug <name>",
            Execute = (ctx, args) =>
            {
                var name = args.Trim();
                if (name.Length == 0) { ctx.LogError("usage: align-debug <name>"); return; }

                OutlinerItemViewModel? Find(IEnumerable<OutlinerItemViewModel> items)
                {
                    foreach (var item in items)
                    {
                        if (item.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && !item.IsToolpath) return item;
                        if (Find(item.Children) is { } found) return found;
                    }
                    return null;
                }

                var piece = Find(ctx.Main.Viewport.OutlinerItems);
                if (piece is null) { ctx.LogError($"[debug] no outliner item named '{name}'."); return; }

                if (piece.Node.Mesh?.PickingData is { } mesh)
                {
                    var w = piece.Node.WorldTransform;
                    var min = new System.Numerics.Vector3(float.MaxValue);
                    var max = new System.Numerics.Vector3(float.MinValue);
                    foreach (var p in mesh.Positions)
                    {
                        var wp = OpenTK.Mathematics.Vector3.TransformPosition(p, w);
                        min = System.Numerics.Vector3.Min(min, new System.Numerics.Vector3(wp.X, wp.Y, wp.Z));
                        max = System.Numerics.Vector3.Max(max, new System.Numerics.Vector3(wp.X, wp.Y, wp.Z));
                    }
                    ctx.Log($"[debug] mesh world AABB: min=({min.X:0.#},{min.Y:0.#},{min.Z:0.#}) max=({max.X:0.#},{max.Y:0.#},{max.Z:0.#})");
                }
                else
                {
                    ctx.LogError("[debug] no mesh geometry on this item.");
                }

                var tpItem = piece.Children.FirstOrDefault(c => c.IsToolpath);
                if (tpItem is null) { ctx.LogError("[debug] no toolpath child."); return; }
                var snap = ctx.Main.Viewport.GetToolpathSnapshot?.Invoke(tpItem.Node);
                if (snap is null) { ctx.LogError("[debug] no toolpath snapshot staged."); return; }

                var tmin = new System.Numerics.Vector3(float.MaxValue);
                var tmax = new System.Numerics.Vector3(float.MinValue);
                int moveCount = 0;
                foreach (var layer in snap.Smoothed.Layers)
                {
                    foreach (var mv in layer.Moves)
                    {
                        tmin = System.Numerics.Vector3.Min(tmin, System.Numerics.Vector3.Min(mv.From, mv.To));
                        tmax = System.Numerics.Vector3.Max(tmax, System.Numerics.Vector3.Max(mv.From, mv.To));
                        moveCount++;
                    }
                }
                if (moveCount == 0) { ctx.LogError("[debug] toolpath has zero moves."); return; }
                ctx.Log($"[debug] toolpath RAW world AABB ({moveCount} moves): min=({tmin.X:0.#},{tmin.Y:0.#},{tmin.Z:0.#}) max=({tmax.X:0.#},{tmax.Y:0.#},{tmax.Z:0.#})");
            },
        });

        Register(new ConsoleCommandDefinition
        {
            Name = "delete-selected",
            Description = "Delete the currently-selected outliner item, via its real DeleteCommand (same as clicking the trash icon)",
            Execute = (ctx, _) =>
            {
                var item = ctx.Main.Viewport.SelectedOutlinerItem;
                if (item is null) { ctx.LogError("[delete] nothing selected."); return; }
                if (!item.CanDelete) { ctx.LogError($"[delete] \"{item.Name}\" can't be deleted."); return; }
                var name = item.Name;
                item.DeleteCommand.Execute(null);
                ctx.Log($"[delete] deleted \"{name}\".");
            },
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
            Description = "Switch to a robot cell by name (e.g. cell LFAM 3). Append --home to reset viewport pose on the active cell.",
            Usage = "cell <name> [--home]",
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
                var p = args.Split((char[])[' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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
                var p = args.Split((char[])[' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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
            Description = "Move external axis E1 while holding A1–A6 (deg on rotary, mm on LFAM 1 rail)",
            Usage = "move-e1 <value> [vel%]",
            Execute = (ctx, args) =>
            {
                var p = args.Split((char[])[' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (p.Length < 1 || !double.TryParse(p[0], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var value))
                {
                    ctx.LogError("usage: move-e1 <value> [vel%]   e.g.  move-e1 0  20   move-e1 -2000");
                    return;
                }
                int vel = p.Length >= 2 && int.TryParse(p[1], out var v) ? v : 20;
                _ = ctx.Main.MoveE1Async(value, vel);
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
            Name = "check-build-freshness",
            Aliases = ["build-freshness", "check-baseline"],
            Description = "Re-run the origin/main freshness check now (same one that fires once at launch)",
            Execute = (ctx, _) =>
            {
                ctx.Log($"[build] this build: baseline {MassiveSlicer.App.BuildInfo.Baseline}, branch {MassiveSlicer.App.BuildInfo.Branch}, delta {MassiveSlicer.App.BuildInfo.Delta}. Checking origin/main...");
                BuildFreshnessChecker.CheckAsync(ctx.Main.StatusBar);
                ctx.Log($"[build] check kicked off in the background — watch the status bar for yellow, or run this again in a moment.");
            },
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
            Description = "Invalidate cell + GLB caches and reload the active cell scene",
            Execute = (ctx, _) =>
            {
                var path = ctx.Main.Viewport.ActiveCellPath;
                if (path is null)
                {
                    ctx.LogError("[cell] No active cell to reload.");
                    return;
                }

                int assets = CellSceneCache.Invalidate(path);
                ctx.Log($"[cell] reloading {Path.GetFileNameWithoutExtension(path)} ({assets} mesh asset(s) refreshed)…");
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
        var parts = (args ?? string.Empty).Split((char[])[' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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

        var parts = args.Split((char[])[' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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
        var p = args.Split((char[])[' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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
        var p = args.Split((char[])[' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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

    /// <summary>
    /// Traces one support's baked pocket layer by layer: where the wrap actually landed,
    /// and how long the neck is. Answers "is the pocket staying put as the wall builds?"
    /// with numbers — the pocket footprint is fixed data, so the wrap centroid should be
    /// identical on every affected layer while only the NECK length varies to reach a
    /// wandering wall. Any drift in the centroid is a real bug, not a perception.
    /// </summary>
    private static void EvalSupportTrace(
        ConsoleCommandContext ctx, StructuralSupportSpec spec, string name)
    {
        var vp = ctx.Main.Viewport;
        if (vp.ActiveScrubToolpath is not { Layers.Count: > 0 } tp)
        {
            ctx.LogError("[support trace] no active toolpath — slice, then select the toolpath");
            return;
        }

        var outline = spec.BuildOutline();
        if (outline.Length < 3) { ctx.LogError("[support trace] degenerate outline"); return; }
        // Match tolerance against the pocket size, not an absolute — a circle's facets sit
        // slightly inside the ideal outline.
        float tol = MathF.Max(1.0f, MathF.Min(spec.WidthMm, spec.DepthMm) * 0.05f);

        bool NearOutline(System.Numerics.Vector3 p)
        {
            foreach (var v in outline)
            {
                float dx = p.X - v.X, dy = p.Y - v.Y;
                if (dx * dx + dy * dy <= tol * tol) return true;
            }
            return false;
        }

        int lo = Math.Max(0, spec.AnchorLayer - Math.Max(0, spec.LayersDown));
        int hi = Math.Min(tp.Layers.Count - 1, spec.AnchorLayer + Math.Max(0, spec.LayersUp));
        ctx.Log($"[support trace] {name} · expected on layers L{lo + 1}..L{hi + 1} "
            + $"({hi - lo + 1} layer(s)) · pocket centre ({spec.CenterX:0.#}, {spec.CenterY:0.#}) "
            + $"· vertex match tol {tol:0.#} mm");

        int layersHit = 0, shown = 0;
        float cxMin = float.MaxValue, cxMax = float.MinValue;
        float cyMin = float.MaxValue, cyMax = float.MinValue;
        float neckMin = float.MaxValue, neckMax = float.MinValue;

        for (int li = lo; li <= hi; li++)
        {
            var moves = tp.Layers[li].Moves;
            double sx = 0, sy = 0;
            int n = 0;
            float neck = -1f;
            foreach (var mv in moves)
            {
                if (mv.Kind != MoveKind.Extrude) continue;
                bool a = NearOutline(mv.From), b = NearOutline(mv.To);
                if (a && b) { sx += mv.To.X; sy += mv.To.Y; n++; }
                // Neck: exactly one end on the outline, the other out on the wall.
                else if (a ^ b)
                {
                    float len = System.Numerics.Vector3.Distance(mv.From, mv.To);
                    if (len > neck) neck = len;
                }
            }
            if (n == 0) continue;

            layersHit++;
            float cx = (float)(sx / n), cy = (float)(sy / n);
            cxMin = MathF.Min(cxMin, cx); cxMax = MathF.Max(cxMax, cx);
            cyMin = MathF.Min(cyMin, cy); cyMax = MathF.Max(cyMax, cy);
            if (neck > 0f) { neckMin = MathF.Min(neckMin, neck); neckMax = MathF.Max(neckMax, neck); }

            if (shown < 8)
            {
                shown++;
                ctx.Log($"  L{li + 1}: wrap centroid ({cx:0.##}, {cy:0.##}) · {n} wrap move(s) · "
                    + (neck > 0f ? $"neck {neck:0.#} mm" : "no neck move found"));
            }
        }

        if (layersHit == 0)
        {
            ctx.Log("[support trace] pocket NOT FOUND in the baked toolpath on any expected "
                + "layer — either the slice predates this support (press Update Slice) or the "
                + "planner never spliced it.");
            return;
        }

        float driftX = cxMax - cxMin, driftY = cyMax - cyMin;
        ctx.Log($"[support trace] {layersHit}/{hi - lo + 1} layer(s) carry the pocket · "
            + $"centroid drift X={driftX:0.###} mm Y={driftY:0.###} mm");
        if (neckMin <= neckMax)
            ctx.Log($"[support trace] neck length range {neckMin:0.#}..{neckMax:0.#} mm "
                + $"(varies by {neckMax - neckMin:0.#} mm — this SHOULD vary as the wall moves)");
        ctx.Log(driftX + driftY < 0.05f
            ? "[support trace] VERDICT: pocket is rock-steady across layers (drift ~0)."
            : $"[support trace] VERDICT: pocket DRIFTS {driftX:0.##}/{driftY:0.##} mm across "
              + "layers — that is a real bug, the footprint is fixed data and must not move.");
    }

    /// <summary>
    /// Measures the Structural Support neck in the LIVE toolpath: finds each pair of
    /// extrude moves that run exactly back along each other (the neck out / neck back)
    /// and reports their centreline separation against the bead width. Answers "do the
    /// two neck passes overlap?" numerically instead of by eye — the bead is drawn
    /// centred on the centreline (half the bead width each side), so a separation below
    /// one bead width means the passes overlap by the difference.
    /// </summary>
    /// <summary>Shortest distance from (x,y) to a closed outline polygon's edges.</summary>
    private static float DistToOutline(System.Numerics.Vector2[] poly, float x, float y)
    {
        float best = float.MaxValue;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
        {
            var a = poly[j];
            var b = poly[i];
            float abx = b.X - a.X, aby = b.Y - a.Y;
            float len2 = abx * abx + aby * aby;
            float t = len2 < 1e-12f
                ? 0f
                : Math.Clamp(((x - a.X) * abx + (y - a.Y) * aby) / len2, 0f, 1f);
            float cx = a.X + t * abx, cy = a.Y + t * aby;
            float d = MathF.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
            if (d < best) best = d;
        }
        return best;
    }

    private static void EvalSupportNeck(ConsoleCommandContext ctx)
    {
        var vp = ctx.Main.Viewport;
        var add = ctx.Main.RightPanel.Additive;

        if (add.StructuralSupports.Count == 0)
        {
            ctx.LogError("[support neck] no structural supports to measure");
            return;
        }
        if (vp.ActiveScrubToolpath is not { Layers.Count: > 0 } tp)
        {
            ctx.LogError("[support neck] no active toolpath — slice, then select the toolpath "
                + "(or enter edit mode) so a scrub is armed");
            return;
        }

        float bead = (float)add.BeadWidth;
        if (bead < 0.5f) bead = 6f;
        int specIdx = add.SelectedSupportIndex;
        if (specIdx < 0 || specIdx >= add.StructuralSupports.Count) specIdx = 0;
        var spec = add.StructuralSupports[specIdx];
        var outline = spec.BuildOutline();

        ctx.Log($"[support neck] {add.SupportNameAt(specIdx)} · bead={bead:0.#} mm, deposited "
            + $"centred on the path (±{bead * 0.5f:0.#} mm). Touching-but-not-overlapping "
            + $"means centrelines {bead:0.#} mm apart.");

        // A retraced pair (one move run exactly backwards by another) is the signature of
        // the old fully-overlapping arm. Scan ahead, not just adjacent — the wrap sits
        // between the two legs.
        const float tol = 0.01f;
        int retraced = 0;
        int layersChecked = 0, wallOk = 0, armOk = 0, pocketOk = 0;
        int shownBad = 0, shownGood = 0;

        int lo = Math.Max(0, spec.AnchorLayer - Math.Max(0, spec.LayersDown));
        int hi = Math.Min(tp.Layers.Count - 1, spec.AnchorLayer + Math.Max(0, spec.LayersUp));

        for (int li = lo; li <= hi; li++)
        {
            var moves = tp.Layers[li].Moves;
            var ex = moves.Where(m => m.Kind == MoveKind.Extrude).ToList();
            if (ex.Count == 0) continue;

            // Classify by PROXIMITY to the outline, not point-in-polygon: every wrap vertex
            // sits exactly ON the boundary, which a containment test excludes — that made
            // this report zero wrap moves and an "arm gap" that was really the pocket
            // diagonal. Same approach `support trace` already uses.
            float onTol = MathF.Max(0.5f, bead * 0.3f);
            bool OnPocket(System.Numerics.Vector3 p) => DistToOutline(outline, p.X, p.Y) <= onTol;
            var legs = ex.Where(m => OnPocket(m.From) ^ OnPocket(m.To)).ToList();
            if (legs.Count == 0) continue;
            layersChecked++;

            for (int i = 0; i < ex.Count; i++)
                for (int j = i + 1; j < Math.Min(ex.Count, i + 64); j++)
                    if (System.Numerics.Vector3.Distance(ex[i].From, ex[j].To) <= tol
                        && System.Numerics.Vector3.Distance(ex[i].To, ex[j].From) <= tol
                        && System.Numerics.Vector3.Distance(ex[i].From, ex[i].To) > 1f)
                        retraced++;

            // Gap 2 — arm: two legs, one bead apart at their outboard (wall-side) ends.
            float armGap = -1f;
            if (legs.Count == 2)
            {
                var e0 = OnPocket(legs[0].From) ? legs[0].To : legs[0].From;
                var e1 = OnPocket(legs[1].From) ? legs[1].To : legs[1].From;
                armGap = System.Numerics.Vector3.Distance(e0, e1);
                if (MathF.Abs(armGap - bead) < bead * 0.35f) armOk++;
            }

            // Gap 1 — wall: the two leg roots should NOT be bridged by another extrude.
            if (legs.Count == 2 && armGap > 0f) wallOk++;

            // Gap 3 — pocket: the wrap must be an open loop (its ends separated).
            var wrap = ex.Where(m => OnPocket(m.From) && OnPocket(m.To)).ToList();
            float pocketGap = wrap.Count > 0
                ? System.Numerics.Vector3.Distance(wrap[0].From, wrap[^1].To)
                : -1f;
            if (pocketGap > 0.5f) pocketOk++;

            // Report FAILING layers by preference — a sample of passing ones proves nothing
            // about the ones that don't.
            bool layerOk = legs.Count == 2 && MathF.Abs(armGap - bead) < bead * 0.35f
                && pocketGap > 0.5f;
            if (!layerOk && shownBad < 6)
            {
                shownBad++;
                float wallMoveLen = legs.Count == 2
                    ? ex.Where(m => !OnPocket(m.From) && !OnPocket(m.To))
                        .Select(m => System.Numerics.Vector3.Distance(m.From, m.To))
                        .DefaultIfEmpty(-1f).Min()
                    : -1f;
                ctx.Log($"  FAIL L{li + 1}: legs={legs.Count} · arm/wall mouth {armGap:0.##} mm "
                    + $"(want {bead:0.#}) · pocket mouth {pocketGap:0.##} mm · shortest wall "
                    + $"move here {wallMoveLen:0.##} mm");
            }
            else if (layerOk && shownGood < 2)
            {
                shownGood++;
                ctx.Log($"  ok   L{li + 1}: legs={legs.Count} · arm/wall mouth {armGap:0.##} mm · "
                    + $"pocket mouth {pocketGap:0.##} mm");
            }
        }

        if (layersChecked == 0)
        {
            ctx.Log("[support neck] no support geometry found in the baked toolpath — "
                + "press Update Slice first.");
            return;
        }

        ctx.Log($"[support neck] {layersChecked} layer(s) checked · "
            + $"wall break {wallOk}/{layersChecked} · arm gap {armOk}/{layersChecked} · "
            + $"pocket break {pocketOk}/{layersChecked} · retraced pairs {retraced}");
        bool allGood = retraced == 0 && wallOk == layersChecked
            && armOk == layersChecked && pocketOk == layersChecked;
        ctx.Log(allGood
            ? "[support neck] VERDICT: all three gaps present on every layer, nothing "
              + "self-overlapping. This is what you asked for."
            : "[support neck] VERDICT: NOT clean — "
              + (retraced > 0 ? $"{retraced} retraced (fully overlapping) pair(s); " : "")
              + $"arm gap wrong on {layersChecked - armOk} layer(s), pocket still closed on "
              + $"{layersChecked - pocketOk} layer(s).");
    }

    /// <summary>
    /// Report support coverage for edit selection (or every island on the current scrub
    /// layer). A mid-bead is "unsupported" when its XY gap to the previous layer exceeds
    /// 0.5× bead width (same threshold as <see cref="Core.Slicing.SliceLayerAnalyzer"/>).
    /// </summary>
    private static void EvalPaintSupport(ConsoleCommandContext ctx, bool wholeLayer)
    {
        var vp = ctx.Main.Viewport;
        if (vp.ActiveScrubToolpath is not { Layers.Count: > 0 } tp)
        {
            ctx.LogError("[paint support] no active toolpath — arm a scrub / enter edit");
            return;
        }

        float bead = (float)ctx.Main.RightPanel.Additive.BeadWidth;
        if (bead < 0.5f) bead = 6f;
        float thr = bead * 0.5f; // OverhangScore ≥ 0.5 ⇔ gap ≥ 0.5× bead

        // Build candidate spans: selection rows, or every contour on the scrub high layer.
        var spans = new List<(int LayerIdx, int Start, int Count, string Label)>();
        if (!wholeLayer && vp.PaintSelectionItems.Count > 0)
        {
            foreach (var item in vp.PaintSelectionItems)
            {
                spans.Add((item.LayerIndex, item.MoveStart, item.MoveCount,
                    $"{item.Title}  ({item.Detail})"));
            }
        }
        else
        {
            // Prefer layer of first selection; else scrub high (1-based → 0-based).
            int li = wholeLayer || vp.PaintSelectionItems.Count == 0
                ? Math.Clamp((int)Math.Round(vp.ToolpathScrubLayerHigh) - 1, 0, tp.Layers.Count - 1)
                : vp.PaintSelectionItems[0].LayerIndex;
            li = Math.Clamp(li, 0, tp.Layers.Count - 1);
            var layer = tp.Layers[li];
            IReadOnlyList<ContourSpan> contours = layer.Contours.Count > 0
                ? layer.Contours
                : SynthesizeExtrudeRuns(layer);
            int n = 0;
            foreach (var c in contours)
            {
                if (c.Count < 1) continue;
                n++;
                string kind = c.Closed ? "closed" : "open";
                spans.Add((li, c.Start, c.Count,
                    $"L{li + 1} island #{n} · {kind} · m{c.Start}+{c.Count}"));
            }
            ctx.Log($"[paint support] layer L{li + 1} (Z={layer.Z:0.#} mm) — {spans.Count} island(s)");
        }

        if (spans.Count == 0)
        {
            ctx.Log("[paint support] nothing to evaluate — select paths in edit mode, or: paint support layer");
            return;
        }

        ctx.Log($"[paint support] bead={bead:0.#} mm  unsupported if XY gap to layer below ≥ {thr:0.##} mm");
        int failCount = 0;
        for (int si = 0; si < spans.Count; si++)
        {
            var (layerIdx, start, count, label) = spans[si];
            if (layerIdx < 0 || layerIdx >= tp.Layers.Count)
            {
                ctx.Log($"  [{si + 1}] {label}  →  invalid layer");
                continue;
            }
            var layer = tp.Layers[layerIdx];
            ToolpathLayer? prev = layerIdx > 0 ? tp.Layers[layerIdx - 1] : null;

            int end = Math.Min(layer.Moves.Count, start + Math.Max(0, count));
            double totalLen = 0, unsupLen = 0;
            int samples = 0, unsupSamples = 0;
            float minGap = float.MaxValue, maxGap = 0f, sumGap = 0f;
            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            System.Numerics.Vector3 midSum = default;
            int midN = 0;

            for (int i = start; i < end; i++)
            {
                var mv = layer.Moves[i];
                if (mv.Kind != MoveKind.Extrude) continue;
                float dist = System.Numerics.Vector3.Distance(mv.From, mv.To);
                totalLen += dist;
                samples++;
                var mid = (mv.From + mv.To) * 0.5f;
                midSum += mid;
                midN++;
                if (mid.X < minX) minX = mid.X;
                if (mid.X > maxX) maxX = mid.X;
                if (mid.Y < minY) minY = mid.Y;
                if (mid.Y > maxY) maxY = mid.Y;

                float gap = prev is null
                    ? float.PositiveInfinity
                    : NearestPrevGapXy(prev, mid);
                if (gap < minGap) minGap = gap;
                if (gap > maxGap && !float.IsInfinity(gap)) maxGap = gap;
                if (!float.IsInfinity(gap)) sumGap += gap;

                if (prev is null || gap >= thr)
                {
                    unsupLen += dist;
                    unsupSamples++;
                }
            }

            double unsupPct = totalLen > 1e-6 ? unsupLen / totalLen * 100.0 : 0;
            float avgGap = samples > 0 && minGap < float.MaxValue ? sumGap / samples : float.NaN;
            bool fails = prev is null
                ? samples > 0
                : unsupPct >= 50.0; // majority of length has no support within 0.5 bead
            if (fails) failCount++;

            string verdict = prev is null
                ? "NO LAYER BELOW (first layer / bed only)"
                : fails
                    ? "UNSUPPORTED — likely print fail without added support"
                    : unsupPct > 5
                        ? "PARTIAL overhang — review"
                        : "supported";

            var centroid = midN > 0 ? midSum / midN : default;
            ctx.Log(
                $"  [{si + 1}] {label}\n"
                + $"      len={totalLen:0.#} mm  samples={samples}  unsup={unsupPct:0.#}% ({unsupLen:0.#} mm)\n"
                + $"      gap to below: min={FmtGap(minGap)}  avg={FmtGap(avgGap)}  max={FmtGap(maxGap)}\n"
                + $"      mid≈({centroid.X:0.#},{centroid.Y:0.#},{centroid.Z:0.#})  XY span {Math.Max(0, maxX - minX):0.#}×{Math.Max(0, maxY - minY):0.#} mm\n"
                + $"      → {verdict}");
        }

        ctx.Log(failCount == 0
            ? $"[paint support] {spans.Count} path(s): all have support under the 0.5×bead rule"
            : $"[paint support] {failCount}/{spans.Count} path(s) FAIL support check");
    }

    private static string FmtGap(float g)
        => float.IsInfinity(g) || float.IsNaN(g) ? "∞" : $"{g:0.##} mm";

    private static float NearestPrevGapXy(ToolpathLayer prev, System.Numerics.Vector3 mid)
    {
        float best = float.MaxValue;
        foreach (var mv in prev.Moves)
        {
            if (mv.Kind != MoveKind.Extrude) continue;
            float d = DistPointToSeg2D(mid.X, mid.Y, mv.From.X, mv.From.Y, mv.To.X, mv.To.Y);
            if (d < best) best = d;
        }
        return best == float.MaxValue ? float.PositiveInfinity : best;
    }

    private static float DistPointToSeg2D(float px, float py, float ax, float ay, float bx, float by)
    {
        float abx = bx - ax, aby = by - ay;
        float len2 = abx * abx + aby * aby;
        float t = len2 < 1e-12f ? 0f : Math.Clamp(((px - ax) * abx + (py - ay) * aby) / len2, 0f, 1f);
        float cx = ax + t * abx - px, cy = ay + t * aby - py;
        return MathF.Sqrt(cx * cx + cy * cy);
    }

    private static List<ContourSpan> SynthesizeExtrudeRuns(ToolpathLayer layer)
    {
        var spans = new List<ContourSpan>();
        var moves = layer.Moves;
        int i = 0;
        while (i < moves.Count)
        {
            while (i < moves.Count && moves[i].Kind != MoveKind.Extrude) i++;
            if (i >= moves.Count) break;
            int start = i;
            while (i < moves.Count && moves[i].Kind == MoveKind.Extrude
                   && !moves[i].IsLayerStitch && !moves[i].IsLayerChange)
                i++;
            int count = i - start;
            if (count < 1) continue;
            bool closed = false;
            if (count >= 2)
            {
                var a = moves[start].From;
                var b = moves[start + count - 1].To;
                closed = System.Numerics.Vector3.DistanceSquared(a, b) < 1.0f;
            }
            spans.Add(new ContourSpan(start, count, closed, -1));
        }
        return spans;
    }
}