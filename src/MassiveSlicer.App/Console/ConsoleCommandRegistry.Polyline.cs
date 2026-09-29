using MassiveSlicer.App.Views;

namespace MassiveSlicer.App.Console;

public sealed partial class ConsoleCommandRegistry
{
    void RegisterPolylineExport()
    {
        Register(new ConsoleCommandDefinition
        {
            Name = "export-polyline",
            Aliases = ["polyline", "export-obj"],
            Description = "Export the active toolpath as a Blender OBJ polyline (millimeters)",
            Usage = "export-polyline [path.obj]",
            Execute = (ctx, args) =>
            {
                var view = ViewportView.PolylineExportView;
                if (view is null)
                {
                    ctx.LogError("[polyline] viewport is not up.");
                    return;
                }

                var path = args.Trim().Trim('"');
                if (path.Length == 0)
                {
                    _ = view.ExportPolylineAsync();
                    ctx.Log("[polyline] save dialog open.");
                    return;
                }

                _ = ExportPolylineTo(ctx, view, path);
            },
        });
    }

    static async Task ExportPolylineTo(ConsoleCommandContext ctx, ViewportView view, string path)
    {
        try
        {
            var written = await view.ExportPolylineToPathAsync(path);
            if (written is null)
                ctx.LogError("[polyline] export failed — select a toolpath, or the path was empty.");
            else
                ctx.Log($"[polyline] wrote {written}");
        }
        catch (Exception ex)
        {
            ctx.LogError($"[polyline] {ex.Message}");
        }
    }
}
