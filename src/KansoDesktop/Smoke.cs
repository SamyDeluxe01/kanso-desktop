using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace KansoDesktop;

internal static class Smoke
{
    // No Show(), desktop takeover or injected input: safe while the user keeps working.
    internal static void DragChecks(string output)
    {
        Directory.CreateDirectory(output);
        var fixture = Path.Combine(output, "fixture"); Directory.CreateDirectory(fixture);
        string original = Path.Combine(fixture, "keep.txt"); File.WriteAllText(original, "Original stays here.");
        var attributes = File.GetAttributes(original);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            var root = Path.Combine(output, "isolated-config");
            using var c = new Controller(root, new[] { fixture });
            PumpUntil(() => !c.Scanning);
            var screen = System.Windows.Forms.Screen.PrimaryScreen!;
            int x = screen.WorkingArea.Left + 140, y = screen.WorkingArea.Top + 140;
            const string recycle = "shell:::{645FF040-5081-101B-9F08-00AA002F954E}";
            var system = new DesktopItem(recycle, "Papierkorb", true);
            var shellData = new DataObject(); shellData.SetData("KansoItem", system);
            if (FenceWindow.ReferenceDropEffect(shellData, DragDropEffects.Link | DragDropEffects.Move) != DragDropEffects.None) throw new InvalidOperationException("Systemsymbol wird fälschlich als Gruppenreferenz angenommen.");
            var fileData = new DataObject(); fileData.SetData("KansoItem", new DesktopItem(original, "keep.txt", false));
            if (fileData.GetDataPresent(DataFormats.FileDrop) || FenceWindow.ReferenceDropEffect(fileData, DragDropEffects.Link | DragDropEffects.Move) != DragDropEffects.Link || FenceWindow.ReferenceDropEffect(fileData, DragDropEffects.Move) != DragDropEffects.None) throw new InvalidOperationException("Interner Drag bietet einen Original-Dateitransfer an.");
            c.DropOnDesktop(system, x, y);
            var native = new NativeIcon(recycle, "Papierkorb", x + 80, y + 80);
            var resolved = c.PositionedSystemIcon(native);
            if (resolved.X != x || resolved.Y != y || c.State.Fences.Any(f => f.Paths.Contains(recycle))) throw new InvalidOperationException("Systemposition wird nicht übernommen oder verändert Gruppen.");
            var scan = c.ScanAsync(); PumpUntil(() => scan.IsCompleted); scan.GetAwaiter().GetResult();
            if (c.Items.Any(i => i.Path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Systemsymbol wird als Dateipfad inventarisiert.");
            var saved = c.FlushAsync(); PumpUntil(() => saved.IsCompleted); saved.GetAwaiter().GetResult();
            string reopenedRoot = Path.Combine(output, "reopened"); Directory.CreateDirectory(reopenedRoot); File.Copy(c.Store.FileName, Path.Combine(reopenedRoot, "layout.json"), true);
            using var reopened = new Controller(reopenedRoot, new[] { fixture });
            PumpUntil(() => !reopened.Scanning);
            resolved = reopened.PositionedSystemIcon(native);
            if (resolved.X != x || resolved.Y != y || reopened.Items.Any(i => i.Path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Systemposition übersteht erneutes Laden nicht.");
            reopened.State.DesktopPositions[recycle] = new DesktopPoint(int.MinValue, int.MinValue);
            if (reopened.PositionedSystemIcon(native) != native) throw new InvalidOperationException("Nicht mehr erreichbare Systemposition wird nicht zurückgesetzt.");
            if (File.ReadAllText(original) != "Original stays here." || File.GetAttributes(original) != attributes || Directory.GetFileSystemEntries(fixture).Length != 1) throw new InvalidOperationException("Künstliches Original verändert.");
            File.WriteAllText(Path.Combine(output, "drag-result.txt"), "PASS: system icon position applied; survives scan and saved-layout reload without filesystem inventory entry or group assignment; unreachable saved position falls back to native position; ordinary private reference drag accepted as Link; shell group drop and Move-only transfer rejected; no FileDrop advertised; artificial original content, attributes and location unchanged. No windows shown, desktop takeover or real mouse drag. Cursor feedback and actual mouse drop still need user observation.");
        }
        finally { app.Shutdown(); }
    }
    internal static void Run(string output)
    {
        Directory.CreateDirectory(output);
        var fixtures = Path.Combine(output, "fixture-desktop"); Directory.CreateDirectory(fixtures);
        var folder = Directory.CreateDirectory(Path.Combine(fixtures, "Projekte")).FullName;
        File.WriteAllText(Path.Combine(folder, "keep.txt"), "untouched");
        Directory.CreateDirectory(Path.Combine(fixtures, "Referenzen"));
        for (int i = 0; i < 200; i++) File.WriteAllText(Path.Combine(fixtures, "Test-" + i + ".txt"), "fixture");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (FenceWindow.ReferenceDropEffect(DragDropEffects.Copy) != DragDropEffects.Copy || FenceWindow.ReferenceDropEffect(DragDropEffects.Move) != DragDropEffects.None || FenceWindow.ReferenceDropEffect(DragDropEffects.Copy | DragDropEffects.Link) != DragDropEffects.Link) throw new InvalidOperationException("Referenz-Drop meldet einen unzulässigen Dateitransfer.");
        using var c = new Controller(Path.Combine(output, "fixture-data"), new[] { fixtures });
        PumpUntil(() => c.Items.Count >= 202);
        var window = new SettingsWindow(c); window.Show(); Render(window, Path.Combine(output, "settings-overview.png"));
        window.ShowSection(2); Render(window, Path.Combine(output, "settings-themes.png"));
        var before = Directory.GetFileSystemEntries(fixtures).OrderBy(p => p).ToArray();
        c.Preview();
        VerifyLooseInteraction(c);
        string external = Path.Combine(output, "outside-desktop.txt"); File.WriteAllText(external, "Original stays here.");
        c.AssignPaths(new[] { external }, c.State.Fences[0]);
        c.DropOnDesktop(c.Items.Single(i => i.Path == external), 100, 100);
        PumpUntil(() => !c.Scanning);
        var rescan = c.ScanAsync(); PumpUntil(() => rescan.IsCompleted); rescan.GetAwaiter().GetResult();
        if (!c.ItemsFor(null).Any(i => i.Path == external) || c.State.Fences.Any(f => f.Paths.Contains(external)) || File.ReadAllText(external) != "Original stays here.") throw new InvalidOperationException("Freie Referenz außerhalb Desktop geht beim Scan verloren oder verändert das Original.");
        var referenceSave = c.FlushAsync(); PumpUntil(() => referenceSave.IsCompleted); referenceSave.GetAwaiter().GetResult();
        var reopenedRoot = Path.Combine(output, "fixture-reopen"); Directory.CreateDirectory(reopenedRoot);
        File.Copy(c.Store.FileName, Path.Combine(reopenedRoot, "layout.json"), true);
        using (var reopened = new Controller(reopenedRoot, new[] { fixtures }))
        {
            PumpUntil(() => !reopened.Scanning);
            if (!reopened.ItemsFor(null).Any(i => i.Path == external) || !reopened.State.DesktopPositions.ContainsKey(external)) throw new InvalidOperationException("Freie Referenz übersteht Neustart nicht.");
        }
        var drop = new DataObject(); var fileDrop = new System.Collections.Specialized.StringCollection(); fileDrop.AddRange(before); drop.SetFileDropList(fileDrop);
        var watch = Stopwatch.StartNew(); c.Windows.First().ReceiveDrop(drop); var assignmentMs = watch.ElapsedMilliseconds;
        if (assignmentMs > 1500) throw new InvalidOperationException("Mehrfachzuordnung blockiert zu lange.");
        if (!before.SequenceEqual(Directory.GetFileSystemEntries(fixtures).OrderBy(p => p)) || File.ReadAllText(Path.Combine(folder, "keep.txt")) != "untouched") throw new InvalidOperationException("Originale verändert.");
        c.Preview(); var fence = c.Windows.First(); c.State.Animations = false; fence.SetFolded(false);
        PumpUntil(() => fence.DisplayedCount >= 202); Render(fence, Path.Combine(output, "fence-preview.png"));
        if (fence.RealizedTileCount <= 0 || fence.RealizedTileCount >= 202) throw new InvalidOperationException("Gruppensymbole fehlen oder sind nicht virtualisiert.");
        foreach (var theme in Themes.All) { c.State.Theme = theme.Name; c.State.Accent = theme.Accent; c.State.Panel = theme.Panel; c.ApplyAppearance(); }
        var dialog = window.EditGroup(c.State.Fences[0], false, false); Render(dialog, Path.Combine(output, "group-editor.png"));
        var search = Descendants<System.Windows.Controls.TextBox>(dialog).Last();
        var choices = Descendants<System.Windows.Controls.ListBox>(dialog).Single();
        search.Text = "Test-19"; dialog.UpdateLayout();
        if (choices.Items.Count != 11) throw new InvalidOperationException("Gruppensuche filtert nicht nach Namen.");
        search.Text = ""; dialog.UpdateLayout();
        if (choices.Items.Count != 203) throw new InvalidOperationException("Gruppensuche verliert Elemente.");
        dialog.Close();
        if (c.State.Fences[0].Paths.Count != 202) throw new InvalidOperationException("Abgebrochener Editor verändert Zuordnungen.");
        var selected = c.State.Fences[0].Paths.ToArray();
        c.EditGroup(c.State.Fences[0], false, "Projekte", selected, selected.Skip(1));
        if (c.State.Fences[0].Paths.Count != 201) throw new InvalidOperationException("Sammelbearbeitung entfernt Auswahl nicht.");
        c.EditGroup(c.State.Fences[0], false, "Projekte", selected, selected);
        for (int cycle = 0; cycle < 40; cycle++)
        {
            c.State.AutoCollapse = cycle % 2 == 0; c.State.Animations = cycle % 3 == 0; c.ApplyAppearance();
            foreach (var frame in c.Windows.ToArray()) { frame.SetFolded(true); frame.SetFolded(false); }
            if (cycle % 8 == 0) c.Rebuild();
            Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        }
        if (c.State.Fences[0].Paths.Count != 202 || !before.SequenceEqual(Directory.GetFileSystemEntries(fixtures).OrderBy(p => p))) throw new InvalidOperationException("Wiederholte Anzeigeänderungen verlieren Originale oder Zuordnungen.");
        int beats = 0; var heartbeat = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) }; heartbeat.Tick += (_, _) => beats++; heartbeat.Start();
        var slow = new SingleFlight<int>(); bool timeout = false;
        var blocking = slow.RunAsync(() => { Thread.Sleep(650); return 1; }, TimeSpan.FromMilliseconds(150));
        PumpUntil(() => blocking.IsCompleted);
        try { blocking.GetAwaiter().GetResult(); } catch (TimeoutException) { timeout = true; }
        heartbeat.Stop(); if (!timeout || beats < 3) throw new InvalidOperationException("Oberfläche bleibt bei verzögertem I/O nicht bedienbar.");
        PumpUntil(() => !slow.Busy);
        var saved = c.FlushAsync(); PumpUntil(() => saved.IsCompleted); saved.GetAwaiter().GetResult();
        var round = new LayoutStore(c.Store.FileName).Load(); if (round.Fences[0].Paths.Count != 202) throw new InvalidOperationException("Mehrfachzuordnung nicht vollständig gespeichert.");
        var exportPath = Path.Combine(output, "export.json"); var export = c.ExportAsync(exportPath); PumpUntil(() => export.IsCompleted); export.GetAwaiter().GetResult();
        var oldState = c.State; var badImportPath = Path.Combine(output, "invalid-import.json"); File.WriteAllText(badImportPath, "{broken");
        var badImport = c.ImportAsync(badImportPath); PumpUntil(() => badImport.IsCompleted); bool rejected = false; try { badImport.GetAwaiter().GetResult(); } catch { rejected = true; }
        if (!rejected || !ReferenceEquals(oldState, c.State)) throw new InvalidOperationException("Fehlerhafter Import veränderte das aktive Layout.");
        var imported = c.ImportAsync(exportPath); PumpUntil(() => imported.IsCompleted); imported.GetAwaiter().GetResult();
        if (c.State.Fences[0].Paths.Count != 202) throw new InvalidOperationException("Asynchroner Import verliert Zuordnungen.");
        c.StopDesktop(); window.Close(); VerifyGuard();
        File.WriteAllText(Path.Combine(output, "smoke-result.txt"), $"PASS: stable loose layer after child DPI events; three programmatic menu sessions survive direct refresh; external loose reference survives rescan and restart without moving its original; 202 FileDrop references through actual drop handler in {assignmentMs} ms; originals unchanged; async inventory; UI heartbeat {beats} during deliberately delayed I/O; bounded timeout; background persistence and export/import round-trip; corrupt import rejected without changing active state; virtualized group rendering; searchable editor and cancel; atomic selection edits; 40 repeated appearance/fold cycles and 5 rebuilds; fixed editor footer; four themes; synthetic recovery guard. Automated WPF checks, not injected mouse drags. No real desktop takeover.");
        app.Shutdown();
    }
    internal static void DesktopProbe(string output, bool visualInspection = false)
    {
        Directory.CreateDirectory(output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var looseFixtures = Path.Combine(output, "fixture-desktop");
        if (visualInspection) { Directory.CreateDirectory(looseFixtures); File.WriteAllText(Path.Combine(looseFixtures, "Kanso Klicktest.txt"), "Artificial loose icon for input acceptance.\n"); }
        using var c = new Controller(Path.Combine(output, "isolated-config"), visualInspection ? new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), looseFixtures } : null);
        bool restored = false; int layers = 0, beats = 0;
        try
        {
            PumpUntil(() => !c.Scanning);
            DesktopItem original;
            if (visualInspection)
            {
                c.InteractionTraceFile=Path.Combine(output,"input-trace.txt");
                c.DiagnosticTaskbarWindows = true; c.State.AutoCollapse = false; c.State.Animations = false; c.State.PanelOpacity = .9;
                var screen = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s => !s.Primary && s.Bounds.Width > s.Bounds.Height) ?? System.Windows.Forms.Screen.PrimaryScreen!; c.State.Fences.Clear(); c.State.Fences.Add(new FenceModel { Title = "Kanso Sichtprüfung", X = screen.WorkingArea.Right - 650, Y = screen.WorkingArea.Top + 200 });
                string demo = Path.Combine(output, "Beispiel.txt"); File.WriteAllText(demo, "Only artificial test data."); c.AssignPaths(new[] { demo }, c.State.Fences[0]); original = c.Items.First(i => i.Path == demo);
                string sampleLink = Path.Combine(output, "Editor-Test.lnk"); if (File.Exists(sampleLink)) c.AssignPaths(new[] { sampleLink }, c.State.Fences[0]);
                string looseDemo = Path.Combine(looseFixtures, "Kanso Klicktest.txt");
                c.AssignPaths(new[] { looseDemo }, c.State.Fences[0]);
                c.DropOnDesktop(c.Items.First(i => i.Path == looseDemo), screen.WorkingArea.Right - 300, screen.WorkingArea.Top + 100);
                c.State.Fences.Add(new FenceModel { Title = "Kanso Zielgruppe", X = screen.WorkingArea.Right - 1060, Y = screen.WorkingArea.Top + 200 });
            }
            else original = c.Items.First();
            var attrs = File.GetAttributes(original.Path);
            c.Assign(original, c.State.Fences[0]);
            c.StartDesktop(); PumpUntil(() => !c.StartingDesktop);
            if (!c.DesktopMode) throw new InvalidOperationException(c.LastError);
            layers = c.LooseLayerCount;
            if (c.LooseIcons().Any(i => string.Equals(i.Path, original.Path, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Zugeordnetes Symbol ist außerhalb der Gruppe doppelt.");
            if (File.GetAttributes(original.Path) != attrs) throw new InvalidOperationException("Originalattribute verändert.");
            foreach (var layer in c.DesktopLayers)
            {
                var handle = new System.Windows.Interop.WindowInteropHelper(layer).Handle; if (!DesktopShell.IsWindowVisible(handle) || DesktopShell.GetWindow(handle, 4) != c.Host || (DesktopShell.GetWindowLong(handle, -16) & 0x40000000) != 0) throw new InvalidOperationException("Lose Symbolansicht nicht sichtbar auf Desktop-Ebene.");
                if (!DesktopShell.GetWindowRect(handle, out var rect) || !System.Windows.Forms.Screen.AllScreens.Any(screen => rect.Left >= screen.Bounds.Left - 2 && rect.Top >= screen.Bounds.Top - 2 && rect.Right <= screen.Bounds.Right + 2 && rect.Bottom <= screen.Bounds.Bottom + 2)) throw new InvalidOperationException("Monitorfläche falsch positioniert oder skaliert.");
            }
            var found = DesktopShell.Find(); if (DesktopShell.IsWindowVisible(found.Icons)) throw new InvalidOperationException("Doppelte Windows-Symbolansicht blieb sichtbar.");
            foreach (var window in c.Windows) if (DesktopShell.GetWindow(new System.Windows.Interop.WindowInteropHelper(window).Handle, 4) != c.Host) throw new InvalidOperationException("Rahmen ist nicht auf Desktop-Ebene.");
            if (visualInspection) File.WriteAllText(Path.Combine(output, "visual-ready.txt"), "READY: synthetic group on desktop; original files untouched.");
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) }; timer.Tick += (_, _) => beats++; timer.Start(); var watch = Stopwatch.StartNew(); PumpUntil(() => watch.ElapsedMilliseconds >= (visualInspection ? 240000 : 1400), visualInspection ? 245 : 20); timer.Stop();
            if (beats < 20 || !c.DesktopMode) throw new InvalidOperationException("Desktop-Ansicht reagiert nicht zuverlässig.");
        }
        finally { c.StopDesktop(); restored = DesktopShell.IsWindowVisible(DesktopShell.Find().Icons); app.Shutdown(); }
        if (!restored) throw new InvalidOperationException("Windows-Symbole nicht wiederhergestellt.");
        File.WriteAllText(Path.Combine(output, "desktop-result.txt"), $"PASS: {layers} frameless icon layers; no duplicate native icon view; assigned original unchanged and excluded from loose view; monitor bounds validated; top-level layered windows owned by desktop; {beats} UI heartbeats; Windows symbols restored. Isolated settings; no personal file operations.");
    }
    private static void PumpUntil(Func<bool> condition, int timeoutSeconds = 20)
    {
        if (condition()) return;
        var frame = new DispatcherFrame(); var watch = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (condition() || watch.Elapsed > TimeSpan.FromSeconds(timeoutSeconds)) frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
        if (!condition()) throw new TimeoutException("Fixture-Prüfung hat ihr Zeitlimit überschritten.");
    }
    private static void VerifyLooseInteraction(Controller c)
    {
        var layer = new DesktopIconsWindow(c, System.Windows.Forms.Screen.PrimaryScreen!, IntPtr.Zero);
        try
        {
            layer.Show();
            PumpUntil(() => ((System.Windows.Controls.Canvas)layer.Content).Children.Count > 0);
            var canvas = (System.Windows.Controls.Canvas)layer.Content;
            var tile = (System.Windows.Controls.Border)canvas.Children[0];
            // Drain idle work: the former bubbled child-DPI loop continually replaced this tile.
            for (int i = 0; i < 8; i++) Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            if (!ReferenceEquals(tile, canvas.Children[0]) || !tile.IsLoaded) throw new InvalidOperationException("Lose Symbolfläche baut sich ohne Änderung erneut auf.");
            for (int i = 0; i < 3; i++)
            {
                // Exercise refresh protection without opening an interactive native menu.
                c.MenuInteraction(true);
                if (!c.InteractionActive) throw new InvalidOperationException("Menüsitzung schützt die Symbolquelle nicht.");
                layer.RefreshItems();
                Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                if (!ReferenceEquals(tile, canvas.Children[0])) throw new InvalidOperationException("Aktualisierung trennt die Menüquelle.");
                c.MenuInteraction(false);
                PumpUntil(() => !c.InteractionActive);
            }
        }
        finally { layer.Close(); }
    }
    private static void Render(Window window, string path)
    {
        var visual = (FrameworkElement)window.Content; visual.Measure(new Size(window.Width, window.Height)); visual.Arrange(new Rect(0, 0, window.Width, window.Height)); visual.UpdateLayout();
        visual.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        var bitmap = new RenderTargetBitmap((int)window.Width, (int)window.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }
    private static System.Collections.Generic.IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static void VerifyGuard()
    {
        using var registration = new System.Windows.Forms.ListView(); _ = registration.Handle;
        var fake = DesktopShell.CreateWindowEx(0, "SysListView32", "Kanso test view", unchecked((int)0x10000000), 0, 0, 10, 10, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (fake == IntPtr.Zero) throw new InvalidOperationException("Testfenster fehlt.");
        try { DesktopShell.ShowWindow(fake, 0); using var child = DesktopShell.LaunchSelf("--probe-lifetime"); using var guard = DesktopShell.LaunchSelf("--watchdog", child.Id.ToString(), fake.ToInt64().ToString(), child.StartTime.ToUniversalTime().Ticks.ToString()); if (!child.WaitForExit(8000) || !guard.WaitForExit(8000) || !DesktopShell.IsWindowVisible(fake)) throw new InvalidOperationException("Wiederherstellung fehlgeschlagen."); }
        finally { DesktopShell.DestroyWindow(fake); }
    }
}



