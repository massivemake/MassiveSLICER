using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.App.Behaviors;

/// <summary>
/// Remembers every <see cref="Expander"/>'s open/closed state across sessions. Keyed by an explicit
/// <c>PersistExpander.Key</c> when set, otherwise by the expander's string Header — so all the app's
/// collapsible panels persist with no per-expander markup. State lives in
/// <see cref="AppPreferences.ExpandedPanels"/> (prefs.json): saved on toggle, restored on next launch.
/// <para>
/// The static handlers are armed when <see cref="Store"/> is first assigned (MainWindowViewModel ctor),
/// which runs before the window's expanders load. Restore happens on each expander's Loaded event; a
/// change is only persisted AFTER its saved value has been restored, so the XAML default can't clobber it.
/// </para>
/// </summary>
public static class PersistExpander
{
    private static AppPreferences? _store;
    private static readonly HashSet<Expander> _seen = [];      // expanders that have loaded
    private static readonly HashSet<Expander> _ready = [];     // restored — safe to persist changes

    public static AppPreferences? Store
    {
        get => _store;
        set { _store = value; if (value is not null) foreach (var ex in _seen) ApplySaved(ex); }
    }

    public static readonly AttachedProperty<string?> KeyProperty =
        AvaloniaProperty.RegisterAttached<Expander, string?>("Key", typeof(PersistExpander));

    public static void SetKey(Expander e, string? value) => e.SetValue(KeyProperty, value);
    public static string? GetKey(Expander e) => e.GetValue(KeyProperty);

    static PersistExpander()
    {
        Control.LoadedEvent.AddClassHandler<Expander>((ex, _) => { _seen.Add(ex); ApplySaved(ex); });
        Expander.IsExpandedProperty.Changed.AddClassHandler<Expander>((ex, _) => Persist(ex));
    }

    private static string? KeyFor(Expander ex)
    {
        var explicitKey = GetKey(ex);
        if (!string.IsNullOrWhiteSpace(explicitKey)) return "exp:" + explicitKey;
        if (ex.Header is string s && !string.IsNullOrWhiteSpace(s)) return "exp:" + s;
        return null;   // non-string header and no explicit key → not persisted
    }

    private static void ApplySaved(Expander ex)
    {
        if (_store is null || KeyFor(ex) is not { } key) return;
        _ready.Add(ex);   // mark ready before applying so the restore's own change is allowed to persist
        if (_store.ExpandedPanels.TryGetValue(key, out var saved) && ex.IsExpanded != saved)
            ex.IsExpanded = saved;
    }

    private static void Persist(Expander ex)
    {
        if (!_ready.Contains(ex)) return;   // ignore the XAML-default change before the saved value is restored
        if (_store is null || KeyFor(ex) is not { } key) return;
        if (_store.ExpandedPanels.TryGetValue(key, out var cur) && cur == ex.IsExpanded) return;
        _store.ExpandedPanels[key] = ex.IsExpanded;
        PreferencesLoader.Save(_store);
    }
}
