using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace KansoDesktop;

internal sealed partial class Controller : IDisposable
{
    internal Settings State { get; private set; }
    internal LayoutStore Store { get; }
    internal string LegacyStorageRoot { get; }
    internal readonly List<FenceWindow> Windows = new();
    internal List<DesktopItem> Items { get; private set; } = new();
    internal bool DesktopMode { get; private set; }
    internal IntPtr Host { get; private set; }
    private IntPtr _icons;
    private bool _wasVisible;
    private Process? _watchdog;
    private readonly List<DesktopIconsWindow> _loose = new();
    private List<NativeIcon> _nativeIcons = new();
    private bool _startingDesktop;
    private int _desktopGeneration;
    private string _monitorLayout = "";
    private static string MonitorLayout() => string.Join(";", Forms.Screen.AllScreens.Select(s => $"{s.DeviceName}:{s.Bounds}:{s.WorkingArea}"));
    private readonly Forms.NotifyIcon _tray;
    private readonly DispatcherTimer _scan;
    private readonly DispatcherTimer _saveDelay;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SingleFlight<List<DesktopItem>> _inventory = new();
    private readonly SingleFlight<Settings> _imports = new();
    private readonly SingleFlight<bool> _exports = new();
    private readonly Dispatcher _dispatcher;
    private readonly LatestWriteQueue _writer;
    private bool _exiting, _exitSaved;
    private SettingsWindow? _settings;
    private readonly string[] _desktopRoots;
    private bool _disposed, _refreshQueued;
    private int _openMenus;
    internal bool InteractionActive => DragActive || _openMenus > 0 || System.Windows.Input.Mouse.LeftButton == System.Windows.Input.MouseButtonState.Pressed || System.Windows.Input.Mouse.RightButton == System.Windows.Input.MouseButtonState.Pressed;
    private readonly DispatcherTimer _refreshRetry = new() { Interval = TimeSpan.FromMilliseconds(150) };
    internal void MenuInteraction(bool open) { _openMenus = Math.Max(0, _openMenus + (open ? 1 : -1)); foreach (var frame in Windows.ToArray()) frame.MenuSession(_openMenus > 0); }
    private long _revision;
    internal string LastError { get; private set; } = "";
    internal event Action? StatusChanged;
    internal bool DragActive { get; private set; }
    internal void DragSession(bool active) { DragActive = active; foreach (var frame in Windows.ToArray()) frame.DragSession(active); }
    internal bool DiagnosticTaskbarWindows { get; set; }
    internal bool Scanning => _inventory.Busy;
    internal bool StartingDesktop => _startingDesktop;
    internal int LooseLayerCount => _loose.Count;
    internal string? InteractionTraceFile {get;set;}
    internal void TraceInteraction(string message){if(InteractionTraceFile is string path){var line=DateTimeOffset.Now.ToString("O")+" "+message+Environment.NewLine;_=Task.Run(()=>{try{lock(this)File.AppendAllText(path,line);}catch{}});}}
    internal IEnumerable<Window> DesktopLayers => _loose;

    internal Controller(string? dataRoot = null, string[]? desktopRoots = null, bool showTray = true)
    {
        _dispatcher = Application.Current.Dispatcher;
        _refreshRetry.Tick += (_, _) => { _refreshRetry.Stop(); RefreshItems(); };
        var data = dataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KansoDesktop");
        LegacyStorageRoot = Path.Combine(data, "Groups");
        _desktopRoots = desktopRoots ?? new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory) };
        Store = new LayoutStore(Path.Combine(data, "layout.json")); State = Store.Load();
        State.Language = L.Normalize(State.Language); L.Set(State.Language);
        _writer = new LatestWriteQueue(payload => Store.Save(JsonSerializer.Deserialize<Settings>(payload)!));
        _writer.Failed += ex => { Diagnostics.Write(L.T("Layout speichern"), ex); if (!_disposed) _dispatcher.BeginInvoke(() => Report(L.T("Layout konnte nicht gespeichert werden. Die Originaldateien sind unverändert."), ex)); };
        Items = ReferencedPaths().Select(p => new DesktopItem(p, Path.GetFileName(p), false)).ToList();
        if (State.Fences.Count == 0 && !File.Exists(Store.FileName)) { State.Fences.Add(new FenceModel { Title = "Projekte", X = 90, Y = 170 }); State.Fences.Add(new FenceModel { Title = "Programme", X = 500, Y = 170 }); }
        _tray = new Forms.NotifyIcon { Text = "Kanso Desktop 1.1.0", Icon = File.Exists(Path.Combine(AppContext.BaseDirectory, "Kanso.ico")) ? new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory, "Kanso.ico")) : System.Drawing.SystemIcons.Application, Visible = showTray };
        _tray.DoubleClick += (_, _) => _dispatcher.Invoke(ShowSettings);
        RebuildTrayMenu();
        _saveDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _saveDelay.Tick += (_, _) => { _saveDelay.Stop(); QueueSave(); };
        _scan = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _scan.Tick += (_, _) =>
        {
            if (DesktopMode && (!DesktopShell.IsWindow(Host) || !DesktopShell.IsWindow(_icons))) { StopDesktop(false); Report(L.T("Explorer wurde neu gestartet. Windows-Symbole sind wieder aktiv.")); }
            if (DesktopMode && _monitorLayout != MonitorLayout()) { StopDesktop(false); Report(L.T("Monitoranordnung geändert. Windows-Symbole sind wieder aktiv; Desktop-Modus erneut starten.")); }
            Scan();
        };
        _scan.Start(); Scan();
    }
    internal void Report(string message, Exception? error = null)
    {
        if (_disposed) return;
        LastError = message; if (error != null) Diagnostics.Write(message, error); StatusChanged?.Invoke();
    }
    internal void Scan() => _ = ScanAsync();
    // A loose icon can reference an original outside Windows' Desktop folders.
    // Keep saved drag-out references in scans and on restart, without creating files.
    private string[] ReferencedPaths() => State.Fences.SelectMany(f => f.Paths).Concat(State.DesktopPositions.Keys).Where(p => !p.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    internal async Task ScanAsync()
    {
        if (_disposed || _inventory.Busy) return;
        long revision = _revision;
        var paths = ReferencedPaths();
        try
        {
            var items = await _inventory.RunAsync(() => DesktopInventory.Read(_desktopRoots, paths), TimeSpan.FromSeconds(4), _lifetime.Token);
            if (_disposed || items == null) return;
            if (revision != _revision) { Scan(); return; }
            bool changed = !Items.SequenceEqual(items);
            Items = items;
            #if NATIVE_PREVIEW
            if (NativePersonalEnabled && DesktopMode && !InteractionActive) PreviewProject(State);
            #endif
            if (changed) RefreshItems();
            StatusChanged?.Invoke();
        }
        catch (OperationCanceledException) { }
        catch (TimeoutException ex) { Report(L.T("Dateisuche wartet auf Windows/OneDrive. Die Oberfläche bleibt bedienbar."), ex); }
        catch (Exception ex) { Report(L.T("Dateiliste konnte nicht aktualisiert werden."), ex); }
    }
    internal IEnumerable<DesktopItem> ItemsFor(FenceModel? model)
    {
        var owned = new HashSet<string>((IEnumerable<string>?)model?.Paths ?? State.Fences.SelectMany(f => f.Paths), StringComparer.OrdinalIgnoreCase);
        return Items.Where(i => model == null ? !owned.Contains(i.Path) : owned.Contains(i.Path));
    }
    internal void Save() { if (_disposed) return; _saveDelay.Stop(); _saveDelay.Start(); }
    private bool QueueSave()
    {
        try { State.Normalize(); _writer.Request(JsonSerializer.Serialize(State)); return true; }
        catch (Exception ex) { Report(L.T("Layout konnte nicht vorbereitet werden."), ex); return false; }
    }
    internal Task FlushAsync()
    {
        _saveDelay.Stop(); return QueueSave() ? _writer.Completion : Task.FromException(new InvalidOperationException(LastError));
    }
    internal async void RequestExit()
    {
        if (_exiting || _disposed) return; _exiting = true;
        StopDesktop(); _scan.Stop(); if (_settings != null) _settings.IsEnabled = false;
        try
        {
            await FlushAsync().WaitAsync(TimeSpan.FromSeconds(3)); _exitSaved = true; Dispose(); Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            if (_settings != null) _settings.IsEnabled = true; _scan.Start(); _exiting = false; ShowSettings(); Report(L.T("Beenden abgebrochen: Das Layout konnte noch nicht sicher gespeichert werden. Erneut versuchen oder Layout exportieren."), ex);
        }
    }
    internal void Assign(DesktopItem item, FenceModel? model) => AssignPaths(new[] { item.Path }, model);
    internal bool CanAcceptDrop(string[] paths, FenceModel? model)
    {
        if (_disposed || paths.Length == 0 || (model != null && !State.Fences.Contains(model))) return false;
        if (paths.Any(p => string.IsNullOrWhiteSpace(p) || p.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))) return false;
        #if NATIVE_PREVIEW
        if (NativePersonalEnabled) return paths.All(NativeReferencePolicy.IsReference);
        if (NativePreviewEnabled && ((!DesktopMode && ProjectionForChecks == null) || paths.Any(p => !_previewPaths.Contains(p, StringComparer.OrdinalIgnoreCase)))) return false;
        #endif
        return true;
    }
    internal void AssignPaths(IEnumerable<string> input, FenceModel? model)
    {
        if (_disposed) return;
        try
        {
            var paths = NormalizeInput(input);
            #if NATIVE_PREVIEW
            if (!PreviewAssignment(paths, model)) return;
#endif
            int changed = ReferenceAssignment.Apply(State, paths, model); if (changed == 0) return;
            var existing = new HashSet<string>(Items.Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths) if (existing.Add(path)) Items.Add(new(path, Path.GetFileName(path), false));
            _revision++; Save(); RefreshItems(); Scan(); StatusChanged?.Invoke();
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is InvalidOperationException || ex is InvalidDataException) { Report(L.T("Zuordnung konnte nicht übernommen werden: ") + ex.Message, ex); }
        static string[] NormalizeInput(IEnumerable<string> values) => values.Where(p => !string.IsNullOrWhiteSpace(p) && !p.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    internal void DropOnDesktop(DesktopItem item, int x, int y)
    {
        #if NATIVE_PREVIEW
        if (NativePreviewEnabled) { AssignPaths(new[] { item.Path }, null); return; }
#endif
        State.DesktopPositions[item.Path] = new DesktopPoint(x, y); ReferenceAssignment.Apply(State, new[] { item.Path }, null); _revision++; Save(); RefreshItems(); StatusChanged?.Invoke();
    }
    internal void RemoveGroup(FenceModel group)
    {
        #if NATIVE_PREVIEW
        if (NativePreviewEnabled && !PreviewAssignment(group.Paths.ToArray(), null)) return;
#endif
        if (!State.Fences.Remove(group)) return; _revision++; Save(); if (Windows.Count > 0) Rebuild(); RefreshItems();
    }
    internal void EditGroup(FenceModel group, bool fresh, string title, IEnumerable<string> considered, IEnumerable<string> selected)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException(L.T("Bitte einen Gruppennamen eingeben."));
        if (!fresh && !State.Fences.Contains(group)) throw new InvalidOperationException(L.T("Die Gruppe wurde inzwischen entfernt."));
        var selection = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
        var removed = considered.Where(p => !selection.Contains(p) && group.Paths.Contains(p, StringComparer.OrdinalIgnoreCase)).ToArray();
        // Validate the entire edit on a detached snapshot before touching the active layout.
        var draft = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(State))!;
        var target = fresh ? new FenceModel { Id = group.Id, Title = title.Trim(), X = group.X, Y = group.Y, Width = group.Width, Height = group.Height } : draft.Fences.First(f => f.Id == group.Id);
        if (fresh) draft.Fences.Add(target);
        target.Title = title.Trim(); ReferenceAssignment.Apply(draft, removed, null); ReferenceAssignment.Apply(draft, selection, target); draft.Normalize();
        #if NATIVE_PREVIEW
        if (NativePreviewEnabled && !PreviewProject(draft)) return;
#endif
        if (fresh) State.Fences.Add(group);
        group.Title = title.Trim(); ReferenceAssignment.Apply(State, removed, null); ReferenceAssignment.Apply(State, selection, group);
        _revision++; Save(); if (Windows.Count > 0) Rebuild(); RefreshItems(); StatusChanged?.Invoke();
    }
    internal void RefreshItems()
    {
        if (_disposed || _refreshQueued) return; _refreshQueued = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, () => { _refreshQueued = false; if (_disposed) return; if (InteractionActive) { _refreshRetry.Start(); return; } foreach (var w in Windows.ToArray()) w.RefreshItems(); foreach (var layer in _loose.ToArray()) layer.RefreshItems(); });
    }
    internal void Rebuild()
    {
        foreach (var w in Windows.ToArray()) w.Close(); Windows.Clear();
        foreach (var model in State.Fences) { var w = new FenceWindow(this, model) { ShowInTaskbar = DiagnosticTaskbarWindows }; Windows.Add(w); w.Show(); if (DesktopMode) w.Attach(Host); }
    }
    internal void Preview() { StopDesktop(); Rebuild(); }
    internal IEnumerable<NativeIcon> LooseIcons()
    {
        var known = _nativeIcons.ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var item in ItemsFor(null))
        {
            if (State.DesktopPositions.TryGetValue(item.Path, out var point) && Forms.Screen.AllScreens.Any(screen => screen.Bounds.Contains(point.X, point.Y))) yield return new NativeIcon(item.Path, item.Name, point.X, point.Y);
            else yield return known.TryGetValue(item.Path, out var icon) && Forms.Screen.AllScreens.Any(screen => screen.Bounds.Contains(icon.X, icon.Y)) ? icon : new NativeIcon(item.Path, item.Name, int.MinValue, int.MinValue);
        }
        foreach (var icon in _nativeIcons.Where(i => i.Path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)))
            yield return PositionedSystemIcon(icon);
    }
    internal NativeIcon PositionedSystemIcon(NativeIcon icon) => State.DesktopPositions.TryGetValue(icon.Path, out var point) && Forms.Screen.AllScreens.Any(screen => screen.Bounds.Contains(point.X, point.Y)) ? icon with { X = point.X, Y = point.Y } : icon;
    internal async void StartDesktop()
    {
        #if NATIVE_PREVIEW
        if (NativePreviewEnabled) { StartNativePreview(); return; }
#endif
        if (DesktopMode || _startingDesktop) return;
        int generation = ++_desktopGeneration; _startingDesktop = true; Report("Desktop-Modus wird vorbereitet …");
        try
        {
            var native = await DesktopIconLayout.ReadIsolatedAsync(_lifetime.Token);
            if (_disposed || generation != _desktopGeneration) return;
            await ScanAsync();
            if (_disposed || generation != _desktopGeneration) return;
            var found = DesktopShell.Find();
            if (found.Host == IntPtr.Zero || found.Icons == IntPtr.Zero || !DesktopShell.IsWindowVisible(found.Icons)) throw new InvalidOperationException("Die normale Windows-Symbolansicht ist nicht verfügbar. Andere Organizer bitte pausieren.");
            if (native.Count == 0 && Items.Count > 0) throw new InvalidOperationException("Keine verlässliche Windows-Symbolansicht verfügbar.");
            foreach (var icon in native.Where(i => !i.Path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))) if (!Items.Any(i => string.Equals(i.Path, icon.Path, StringComparison.OrdinalIgnoreCase))) Items.Add(new(icon.Path, icon.Name, false));
            _nativeIcons = native.GroupBy(i => i.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
            Host = found.Host; _icons = found.Icons;
            _watchdog = DesktopShell.StartWatchdog(_icons);
            await Task.Delay(200, _lifetime.Token);
            if (_disposed || generation != _desktopGeneration) { if (_watchdog != null) { try { _watchdog.Kill(); } catch { } _watchdog.Dispose(); _watchdog = null; } return; }
            if (_watchdog.HasExited) throw new InvalidOperationException("Wiederherstellungswächter ist nicht verfügbar.");
            DesktopMode = true; _monitorLayout = MonitorLayout();
            foreach (var screen in Forms.Screen.AllScreens) { var layer = new DesktopIconsWindow(this, screen, Host); _loose.Add(layer); layer.Show(); }
            Rebuild();
            if (!DesktopMode || _loose.Count != Forms.Screen.AllScreens.Length) throw new InvalidOperationException("Die Symbolanzeige wurde nicht vollständig aufgebaut.");
            _wasVisible = true; DesktopShell.ShowWindow(_icons, 0);
            Report("");
        }
        catch (OperationCanceledException) { if (!_disposed && generation == _desktopGeneration) { StopDesktop(); Report("Desktop-Vorbereitung wurde abgebrochen."); } }
        catch (Exception ex) { StopDesktop(); Report("Desktop-Modus wurde sicher zurückgesetzt.", ex); }
        finally { _startingDesktop = false; if (!_disposed) StatusChanged?.Invoke(); }
    }
    internal void RestoreDesktop() { if (_wasVisible) DesktopShell.Restore(_icons); }
    internal void StopDesktop() => StopDesktop(true);
    internal void StopDesktop(bool rememberChoice)
    {
        #if NATIVE_PREVIEW
        if (NativePersonalEnabled && rememberChoice && !_exiting && !_disposed) { State.DesktopModeRequested = false; Save(); }
        StopNativePreview();
#endif
        LastError = ""; _desktopGeneration++; RestoreDesktop();
        foreach (var layer in _loose.ToArray()) layer.Close(); _loose.Clear();
        DesktopMode = false; _wasVisible = false; Host = IntPtr.Zero;
        foreach (var w in Windows.ToArray()) w.Close(); Windows.Clear();
        if (_watchdog != null) { try { if (!_watchdog.HasExited) _watchdog.Kill(); } catch { } _watchdog.Dispose(); _watchdog = null; }
        StatusChanged?.Invoke();
    }
    internal void ShowSettings() { if (_settings == null) { _settings = new SettingsWindow(this); _settings.Closed += (_, _) => _settings = null; } _settings.Show(); _settings.Activate(); }
    private void RebuildTrayMenu()
    {
        var old = _tray.ContextMenuStrip;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(L.T("Einstellungen"), null, (_, _) => _dispatcher.Invoke(ShowSettings));
        menu.Items.Add(L.T("Windows-Desktop wiederherstellen"), null, (_, _) => _dispatcher.Invoke(StopDesktop));
        menu.Items.Add(L.T("Beenden"), null, (_, _) => _dispatcher.Invoke(RequestExit));
        _tray.ContextMenuStrip = menu; old?.Dispose();
    }
    internal void SetLanguage(string code)
    {
        if (_disposed) return;
        code = L.Normalize(code);
        if (code == State.Language) return;
        State.Language = code; L.Set(code); LastError = ""; Save(); RebuildTrayMenu();
        if (Windows.Count > 0) Rebuild();
        if (_settings != null)
        {
            var previous = _settings;
            int section = previous.CurrentSection;
            double left = previous.Left, top = previous.Top, width = previous.Width, height = previous.Height;
            var windowState = previous.WindowState;
            bool visible = previous.IsVisible;
            _settings = null; previous.Close();
            _settings = new SettingsWindow(this) { WindowStartupLocation = WindowStartupLocation.Manual, Width = width, Height = height, Left = left, Top = top };
            _settings.Closed += (_, _) => _settings = null;
            _settings.ShowSection(section);
            if (visible) { _settings.Show(); _settings.WindowState = windowState; _settings.Activate(); }
        }
        StatusChanged?.Invoke();
    }
    internal void ApplyAppearance() { Save(); foreach (var w in Windows.ToArray()) w.ApplyAppearance(); RefreshItems(); }
    internal async Task ImportAsync(string path)
    {
        #if NATIVE_PREVIEW
        if (NativePreviewEnabled) throw new InvalidOperationException("Im künstlichen Test bleiben persönliche Layouts getrennt. Import ist hier deaktiviert.");
#endif
        if (_imports.Busy) throw new InvalidOperationException(L.T("Ein Import wartet noch auf Windows."));
        var imported = await _imports.RunAsync(() => new LayoutStore(path).Load(), TimeSpan.FromSeconds(4), _lifetime.Token);
        if (_disposed || imported == null) return;
        QueueSave(); State = imported; _revision++; Save(); Scan(); if (Windows.Count > 0) Rebuild(); RefreshItems(); StatusChanged?.Invoke();
    }
    internal async Task ExportAsync(string path)
    {
        if (_exports.Busy) throw new InvalidOperationException(L.T("Ein Export wartet noch auf Windows."));
        var json = JsonSerializer.Serialize(State, new JsonSerializerOptions { WriteIndented = true });
        await _exports.RunAsync(() => { new LayoutStore(path).Save(JsonSerializer.Deserialize<Settings>(json)!); return true; }, TimeSpan.FromSeconds(4), _lifetime.Token);
    }
    internal async void Open(DesktopItem item)
    {
        try { await Task.Run(() => Process.Start(item.Path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ? new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { item.Path } } : new ProcessStartInfo(item.Path) { UseShellExecute = true })); }
        catch (Exception ex) { Report(L.T("Das Element kann nicht geöffnet werden."), ex); }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _scan.Stop(); _saveDelay.Stop(); _refreshRetry.Stop(); if (!_exitSaved) QueueSave(); _disposed = true; _lifetime.Cancel(); StopDesktop(); _tray.Dispose();
        // Normal exit awaited persistence; crash cleanup restores symbols immediately.
    }
}










