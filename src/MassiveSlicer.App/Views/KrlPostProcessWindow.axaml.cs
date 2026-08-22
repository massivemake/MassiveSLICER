using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MassiveSlicer.Core.IO;
using MassiveSlicer.ViewModels;

namespace MassiveSlicer.App.Views;

public partial class KrlPostProcessWindow : Window
{
    private static readonly FilePickerFileType JsonFileType = new("KRL post-process JSON")
    {
        Patterns = ["*.json"],
        MimeTypes = ["application/json"],
        AppleUniformTypeIdentifiers = ["public.json"],
    };

    public KrlPostProcessWindow()
    {
        InitializeComponent();
        DialogWindowChrome.Apply(this);
        TitleBar.PointerPressed += (_, e) => BeginMoveDrag(e);
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        if (DataContext is KrlPostProcessSettingsViewModel vm)
            vm.Save();
        Close();
    }

    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not KrlPostProcessSettingsViewModel vm) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export KRL Post-Processing",
            SuggestedFileName = "krl-postprocess",
            DefaultExtension = "json",
            FileTypeChoices = [JsonFileType],
            ShowOverwritePrompt = true,
        });
        if (file is null) return;
        try
        {
            var json = KrlPostProcessDocument.SerializeEnvelope(vm.ToSettings());
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(json);
            vm.SyncStatus = $"exported {file.Name}";
        }
        catch (Exception ex)
        {
            vm.SyncStatus = $"export failed: {ex.Message}";
        }
    }

    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not KrlPostProcessSettingsViewModel vm) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import KRL Post-Processing",
            AllowMultiple = false,
            FileTypeFilter = [JsonFileType],
        });
        if (files.Count == 0) return;
        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var reader = new StreamReader(stream);
            var json = await reader.ReadToEndAsync();
            if (!KrlPostProcessDocument.TryParse(json, out var settings, out var err))
            {
                vm.SyncStatus = $"import failed: {err}";
                return;
            }
            vm.LoadFrom(settings);
            vm.Save();
            vm.SyncStatus = $"imported {files[0].Name} — Rules + Header + Footer applied";
        }
        catch (Exception ex)
        {
            vm.SyncStatus = $"import failed: {ex.Message}";
        }
    }

    private async void OnPullLab(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not KrlPostProcessSettingsViewModel vm) return;
        if (GetErp() is not { } erp)
        {
            vm.SyncStatus = "open MassiveLAB and connect first";
            return;
        }
        if (!erp.IsConnected)
        {
            vm.SyncStatus = "not connected to Lab";
            return;
        }
        vm.SyncStatus = "pulling from Lab…";
        var summary = await erp.PullKrlPostProcessAsync();
        vm.SyncStatus = summary;
    }

    private async void OnPublishLab(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not KrlPostProcessSettingsViewModel vm) return;
        if (GetErp() is not { } erp)
        {
            vm.SyncStatus = "open MassiveLAB and connect first";
            return;
        }
        if (!erp.IsConnected)
        {
            vm.SyncStatus = "not connected to Lab";
            return;
        }
        vm.Save();
        vm.SyncStatus = "publishing to Lab…";
        var summary = await erp.PublishKrlPostProcessAsync(vm.ToSettings());
        vm.SyncStatus = summary;
    }

    ErpViewModel? GetErp()
    {
        if (Owner is Window { DataContext: MainWindowViewModel main })
            return main.Viewport.Erp;
        return null;
    }
}
