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
            Description = "Toolpath paint marks: bridge/remove dabs, list, clear",
            Usage = "paint <bridge|remove> <x> <y> <z> <radius> | paint list | paint clear",
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
            Name = "selection",
            Aliases = ["selected"],
            Description = "Report the renderer's current selection (what would be highlighted)",
            Execute = (ctx, _) => ctx.Log(ctx.Main.Viewport.DescribeSelection()),
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
}