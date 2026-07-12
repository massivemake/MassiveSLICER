using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;
using MassiveSlicer.ViewModels;

namespace MassiveSlicer.App.Views;

public partial class MaterialPresetDialog : Window
{
    public MaterialPresetDialog()
    {
        InitializeComponent();
        TitleBar.PointerPressed += (_, e) => BeginMoveDrag(e);
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MaterialPresetEditorViewModel vm) return;
        Close(vm.ToPreset());
    }

    private void OnApplyCalibration(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MaterialPresetEditorViewModel vm)
            vm.ApplyCalibration();
    }

    /// <summary>
    /// Writes a calibration <c>.mass</c> workspace and opens it in a <b>new</b> MassiveSLICER
    /// process. Does not change the current scene.
    /// </summary>
    private void OnCreateMaterialCalibration(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MaterialPresetEditorViewModel vm) return;

        CellConfig? cell = null;
        string? cellPath = null;
        float[]? home = null;
        float homeE1 = float.NaN;
        int toolIdx = 1;
        int baseIdx = 1;
        AppPreferences? basePrefs = null;
        MainWindowViewModel? main = null;

        if (Owner is Window owner
            && owner.DataContext is MainWindowViewModel mvm)
        {
            main = mvm;
            cell = mvm.Viewport.ActiveCell;
            cellPath = mvm.Viewport.ActiveCellPath;
            var add = mvm.RightPanel.Additive;
            home = add.SelectedHomeAngles;
            toolIdx = add.ToolDataIndex;
            baseIdx = add.BaseDataIndex;
            basePrefs = mvm.AppPreferences;
            if (cell?.RobotRail is not null && mvm.Viewport.Robot is { } robot)
                homeE1 = (float)robot.E1;
        }

        var preset = vm.ToPreset();
        string savePath = MaterialCalibrationWorkspace.SuggestSavePath(preset.Name);

        try
        {
            savePath = MaterialCalibrationWorkspace.Create(new MaterialCalibrationWorkspace.CreateRequest
            {
                SavePath      = savePath,
                Material      = preset,
                MotorPercent  = (float)vm.CalibMotorPercent,
                RunTimeSec    = (float)vm.CalibTimeSec,
                CellPath      = cellPath,
                Cell          = cell,
                HomeAngles    = home,
                HomeE1Mm      = homeE1,
                ToolDataIndex = toolIdx,
                BaseDataIndex = baseIdx,
                BasePreferences = basePrefs,
            });
        }
        catch (Exception ex)
        {
            main?.Console.LogError($"[material-cal] Failed to write workspace: {ex.Message}");
            return;
        }

        try
        {
            LaunchNewMassiveSlicer(savePath);
            main?.Console.Log(
                $"[material-cal] Opened new MassiveSLICER with calibration workspace:\n  {savePath}\n" +
                "This scene was not changed. Adjust path/settings in the new window, then Export / Send to robot.");
        }
        catch (Exception ex)
        {
            main?.Console.LogError(
                $"[material-cal] Workspace saved to {savePath} but could not launch a new instance: {ex.Message}\n" +
                "Open that .mass file manually (File → Open or double-click).");
        }
    }

    /// <summary>Starts a second MassiveSLICER process with the given .mass path. Never touches this process.</summary>
    private static void LaunchNewMassiveSlicer(string massPath)
    {
        string argsQuoted = $"\"{massPath}\"";
        string? processPath = Environment.ProcessPath;

        // Published apphost / .app binary
        if (!string.IsNullOrEmpty(processPath)
            && !IsDotnetHost(processPath)
            && File.Exists(processPath))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName         = processPath,
                Arguments        = argsQuoted,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute  = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                                   || RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            });
            return;
        }

        // `dotnet run` / `dotnet MassiveSlicer.App.dll`
        string dll = Path.Combine(AppContext.BaseDirectory, "MassiveSlicer.App.dll");
        if (!File.Exists(dll))
            throw new FileNotFoundException("MassiveSlicer.App.dll not found next to the running app.", dll);

        string dotnet = ResolveDotnetPath(processPath);
        Process.Start(new ProcessStartInfo
        {
            FileName         = dotnet,
            Arguments        = $"\"{dll}\" {argsQuoted}",
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute  = false,
        });
    }

    private static bool IsDotnetHost(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        return name.Equals("dotnet", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveDotnetPath(string? processPath)
    {
        if (!string.IsNullOrEmpty(processPath) && IsDotnetHost(processPath) && File.Exists(processPath))
            return processPath;

        // Common install locations when ProcessPath is the apphost under `dotnet run`
        foreach (var candidate in new[]
                 {
                     "/usr/local/share/dotnet/dotnet",
                     "/usr/local/bin/dotnet",
                     "/usr/bin/dotnet",
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                         ".dotnet", "dotnet"),
                 })
        {
            if (File.Exists(candidate)) return candidate;
        }

        return "dotnet"; // rely on PATH
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
