using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace KansoDesktop;

internal sealed partial class Controller
{
    internal bool NativePreviewEnabled { get; private set; }
    private ArtificialDesktopConnection? _previewConnection;
    private string[]? _confirmedNativePaths;
    private string[] _previewPaths = Array.Empty<string>();
    private ulong _previewToken;
    private bool _previewFilesCreated;
    private readonly DispatcherTimer _previewHeartbeat = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private DateTime _previewDeadline;
    internal Func<string[], bool>? ProjectionForChecks;
    internal void ConfigureNativePreview(ulong token, string[] paths)
    {
        NativePreviewEnabled = true; _previewToken = token; _previewPaths = paths;
        _previewHeartbeat.Tick += (_, _) =>
        {
            if ((!NativePersonalEnabled && DateTime.UtcNow >= _previewDeadline) || _previewConnection?.Ping() != true)
            { StopDesktop(false); Report(NativePersonalEnabled ? L.T("Desktop-Verbindung verloren. Windows-Symbole sind wieder freigegeben; Zuordnungen bleiben gespeichert.") : "Künstlicher Test beendet: Zeitlimit oder Verbindung verloren. Windows-Symbole werden freigegeben."); }
        };
    }
    private bool PreviewAssignment(string[] paths, FenceModel? model)
    {
        if (!NativePreviewEnabled) return true;
        if (!NativePersonalEnabled && paths.Any(p => !_previewPaths.Contains(p, StringComparer.OrdinalIgnoreCase)))
        { Report("Dieser Test akzeptiert nur seine drei künstlichen Dateien. Persönliche Zuordnungen bleiben unverändert."); return false; }
        var draft = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(State))!;
        var target = model == null ? null : draft.Fences.SingleOrDefault(f => f.Id == model.Id);
        if (model != null && target == null) return false;
        ReferenceAssignment.Apply(draft, paths, target);
        return PreviewProject(draft);
    }
    private bool PreviewProject(Settings draft)
    {
        string[] paths = draft.Fences.SelectMany(f => f.Paths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (NativePersonalEnabled && paths.Any(p => !NativeReferencePolicy.IsReference(p)))
        { Report(L.T("Es können nur vollständige Datei- oder Ordnerpfade zugeordnet werden.")); return false; }
        if (!NativePersonalEnabled && paths.Any(p => !_previewPaths.Contains(p, StringComparer.OrdinalIgnoreCase)))
        { Report("Persönliche Dateien sind vom künstlichen Test ausgeschlossen."); return false; }
        if (NativePersonalEnabled) paths = paths.Where(p => NativeReferencePolicy.IsDesktopChild(p, _desktopRoots) && !Items.Any(i => i.Missing && string.Equals(i.Path, p, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (ProjectionForChecks != null) return ProjectionForChecks(paths);
        if (!DesktopMode || _previewConnection == null)
        {
            if (NativePersonalEnabled || paths.Length == 0) return true;
            Report("Für Testzuordnungen zuerst den nativen Desktop-Modus aktivieren."); return false;
        }
        if (_confirmedNativePaths != null && paths.SequenceEqual(_confirmedNativePaths, StringComparer.OrdinalIgnoreCase)) return true;
        if (_previewConnection.Update(paths)) { _confirmedNativePaths = paths.ToArray(); return true; }
        StopDesktop(false); Report(L.T("Windows konnte die Zuordnung nicht bestätigen. Desktop-Modus beendet; Originaldateien und gespeicherte Zuordnungen bleiben erhalten.")); return false;
    }
    private void StartNativePreview()
    {
        if (NativePersonalEnabled) { StartPersonalDesktop(); return; }
        if (DesktopMode || _startingDesktop) return;
        var found = DesktopShell.Find();
        if (found.Host == IntPtr.Zero || !DesktopShell.IsWindowVisible(found.Icons))
        { Report("Die vorhandenen Windows-Desktopsymbole müssen sichtbar sein. Andere Organizer bitte selbst regulär beenden."); return; }
        if (MessageBox.Show("Künstlicher Funktionstest mit deiner bisherigen Kanso-Oberfläche.\n\nEs werden drei neue Textdateien auf dem Desktop erzeugt. Nur diese dürfen in die Gruppen gezogen werden. Deine persönlichen Zuordnungen sind nicht geladen.\n\nDas native Modul wird in den vorhandenen Explorer geladen und bleibt nach dem Test inaktiv bis Explorer regulär endet. Ein Fehler kann Desktop oder Taskleiste beeinträchtigen. Der Test endet nach zehn Minuten.\n\nJetzt starten?", "Kanso · nativer Test", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return;
        _startingDesktop = true;
        try
        {
            if (!_previewFilesCreated)
            {
                foreach (string path in _previewPaths)
                {
                    using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                    file.Write(Encoding.UTF8.GetBytes("Kanso künstliche Testdatei. Nach Testende manuell entfernbar.\r\n" + Path.GetFileName(path)));
                }
                _previewFilesCreated = true;
            }
            if (_previewPaths.Any(p => !File.Exists(p))) throw new IOException("Eine künstliche Testdatei fehlt. Für einen neuen Versuch das Testprogramm neu öffnen.");
            _previewConnection = new ArtificialDesktopConnection(_previewToken); _previewConnection.Connect();
            Host = found.Host; _icons = found.Icons; _wasVisible = false;
            foreach (string path in _previewPaths)
            {
                // Retain fixture references in scans without enumerating personal Desktop files.
                State.DesktopPositions[path] = new DesktopPoint(0, 0);
                if (!Items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase))) Items.Add(new(path, Path.GetFileName(path), false));
            }
            DesktopMode = true; _monitorLayout = MonitorLayout();
            Rebuild(); // Exact existing FenceWindow, icon tiles, themes and folding.
            _previewDeadline = DateTime.UtcNow.AddMinutes(10); _previewHeartbeat.Start();
            Save(); Report("Künstlicher nativer Test aktiv. Freie Symbole bleiben im vorhandenen Windows-Desktop. Nur Kanso-Test-Dateien zuordnen.");
        }
        catch (Exception ex) { StopDesktop(); Report("Künstlicher Test konnte nicht starten: " + ex.Message, ex); }
        finally { _startingDesktop = false; StatusChanged?.Invoke(); }
    }
    private void StopNativePreview()
    {
        if (!NativePreviewEnabled) return;
        if (NativePersonalEnabled && (_disposed || _exiting)) _resumeTimer.Stop();
        _previewHeartbeat.Stop(); _previewConnection?.Dispose(); _previewConnection = null; _confirmedNativePaths = null;
        if (!NativePersonalEnabled) foreach (var group in State.Fences) group.Paths.Clear();
        Save();
    }
}

internal static class NativeAppearancePreview
{
    // Clone the current appearance and geometry only. Never import real references.
    internal static Settings CopyAppearance(Settings current)
    {
        var copy = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(current))!;
        foreach (var group in copy.Fences) group.Paths.Clear();
        copy.Inbox.Paths.Clear(); copy.DesktopPositions.Clear();
        return copy;
    }
    internal static void Run(string output)
    {
        using var mutex = new Mutex(false, "Local\\KansoDesktopBeta"); bool owns;
        try { owns = mutex.WaitOne(0); } catch (AbandonedMutexException) { owns = true; }
        if (!owns) { MessageBox.Show("Kanso läuft bereits. Bitte die bisherige Sitzung selbst regulär beenden."); return; }
        try
        {
            string session = Path.Combine(output, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(session);
            var current = new LayoutStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KansoDesktop", "layout.json")).Load();
            var isolated = CopyAppearance(current); new LayoutStore(Path.Combine(session, "layout.json")).Save(isolated);
            ulong token = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)) & 0x7FFFFFFFFFFFFFFFUL; if (token == 0) token = 1;
            string[] paths = new[] { "Alpha", "Beta", "Gamma" }.Select(n => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"Kanso-Test-{token:X16}-{n}.txt")).ToArray();
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            using var controller = new Controller(session, Array.Empty<string>());
            controller.InteractionTraceFile = Path.Combine(session, "drop-dock-local.log");
            controller.ConfigureNativePreview(token, paths);
            app.DispatcherUnhandledException += (_, e) => { controller.StopDesktop(); MessageBox.Show("Test beendet: " + e.Exception.Message); e.Handled = true; app.Shutdown(1); };
            controller.ShowSettings(); controller.Report("Separate Testversion: bisherige Optik und Gruppenanordnung, persönliche Zuordnungen bewusst nicht geladen. Aktivieren startet nur einen künstlichen Test.");
            app.Run();
        }
        finally { mutex.ReleaseMutex(); }
    }
    internal static void Check(string output)
    {
        Directory.CreateDirectory(output); int count = 0;
        void Assert(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); count++; }
        var original = new Settings { Theme = "Anime", Accent = "#ABCDEF", Panel = "#123456", PanelOpacity = .63, AutoCollapse = false, Animations = true, IconSize = 44 };
        original.Fences.Add(new FenceModel { Title = "Beispiel", X = -700, Y = 180, Width = 410, Height = 360, Collapsed = false, Paths = new() { "C:\\ArtificialPersonalReference.txt" } });
        original.Inbox.Paths.Add("C:\\ArtificialInboxReference.txt"); original.DesktopPositions["C:\\ArtificialPosition.txt"] = new DesktopPoint(30, 40);
        string before = JsonSerializer.Serialize(original); var copy = CopyAppearance(original);
        Assert(JsonSerializer.Serialize(original) == before, "Input layout not modified");
        Assert(copy.Fences.Single().Id == original.Fences.Single().Id && copy.Fences.Single().Title == "Beispiel", "Group identity and title preserved");
        Assert(copy.Fences.Single().X == -700 && copy.Fences.Single().Y == 180 && copy.Fences.Single().Width == 410 && copy.Fences.Single().Height == 360, "Geometry preserved");
        Assert(copy.Theme == original.Theme && copy.Accent == original.Accent && copy.Panel == original.Panel && copy.PanelOpacity == original.PanelOpacity && copy.IconSize == original.IconSize, "Appearance preserved");
        Assert(copy.AutoCollapse == original.AutoCollapse && copy.Animations == original.Animations, "Independent behavior switches preserved");
        Assert(copy.Fences.Single().Paths.Count == 0 && copy.Inbox.Paths.Count == 0 && copy.DesktopPositions.Count == 0, "Personal references not imported");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string data = Path.Combine(output, "isolated-ui"); new LayoutStore(Path.Combine(data, "layout.json")).Save(copy);
        using (var controller = new Controller(data, Array.Empty<string>(), false))
        {
            string fixture = Path.Combine(data, "Artificial.txt");
            controller.ConfigureNativePreview(1, new[] { fixture });
            string[] projected = Array.Empty<string>(); bool accepted = true;
            controller.ProjectionForChecks = p => { if (!accepted) return false; projected = p; return true; };
            var target = controller.State.Fences.Single();
            controller.AssignPaths(new[] { fixture }, target);
            Assert(target.Paths.SequenceEqual(new[] { fixture }) && projected.SequenceEqual(new[] { fixture }), "Existing controller commits confirmed projection");
            accepted = false; controller.DropOnDesktop(new(fixture, "Artificial.txt", false), 120, 200);
            Assert(target.Paths.Count == 1, "Rejected native release retains assignment");
            accepted = true; controller.DropOnDesktop(new(fixture, "Artificial.txt", false), 120, 200);
            Assert(target.Paths.Count == 0 && projected.Length == 0 && !controller.State.DesktopPositions.ContainsKey(fixture), "Existing drag-out releases native rule without replacement icon position");
            controller.AssignPaths(new[] { "C:\\ArtificialPersonalReference.txt" }, target);
            Assert(target.Paths.Count == 0 && projected.Length == 0, "Existing controller rejects personal reference");
            var fence = new FenceWindow(controller, controller.State.Fences.Single());
            Assert(fence.AllowsTransparency && fence.WindowStyle == WindowStyle.None, "Existing transparent group implementation");
            Assert(controller.LooseLayerCount == 0 && !controller.DesktopMode, "No replacement Desktop layer created");
            var payload = new DataObject(DataFormats.FileDrop, new[] { fixture });
            fence.QueueDrop(payload);
            Assert(target.Paths.Count == 0, "Drop returns before native projection is called");
            var untilDrop = System.Diagnostics.Stopwatch.StartNew();
            while (target.Paths.Count == 0 && untilDrop.ElapsedMilliseconds < 1500)
            { app.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle); Thread.Sleep(10); }
            Assert(target.Paths.SequenceEqual(new[] { fixture }), "Queued drop commits after source callback returns");
            controller.DropOnDesktop(new(fixture, "Artificial.txt", false), 0, 0);
            Assert(target.Paths.Count == 0, "Queued drop can be released without moving its original");
            Assert(controller.CanAcceptDrop(new[] { fixture }, target), "Current session file admitted before advertising a drop");
            Assert(!controller.CanAcceptDrop(new[] { "C:\\OtherSession.txt" }, target), "Foreign session rejected before advertising a drop");
            Assert(!controller.CanAcceptDrop(new[] { fixture }, new FenceModel()), "Removed group rejected before advertising a drop");
            var routedPayload = new DataObject(DataFormats.FileDrop, new[] { fixture });
            var dragConstructor = typeof(System.Windows.DragEventArgs).GetConstructors(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Single();
            var routedEnter = (System.Windows.DragEventArgs)dragConstructor.Invoke(new object[] { routedPayload, System.Windows.DragDropKeyStates.LeftMouseButton, DragDropEffects.Copy | DragDropEffects.Link, fence, new Point(20,20) });
            routedEnter.RoutedEvent = DragDrop.PreviewDragEnterEvent; fence.RaiseEvent(routedEnter);
            Assert(routedEnter.Effects == DragDropEffects.Link && routedEnter.Handled, "Actual routed enter admits current fixture");
            var routedDrop = (System.Windows.DragEventArgs)dragConstructor.Invoke(new object[] { routedPayload, (System.Windows.DragDropKeyStates)0, DragDropEffects.Copy | DragDropEffects.Link, fence, new Point(20,20) });
            routedDrop.RoutedEvent = DragDrop.PreviewDropEvent; fence.RaiseEvent(routedDrop);
            Assert(target.Paths.Count == 0 && routedDrop.Effects == DragDropEffects.Link, "Routed drop snapshots data without committing inside callback");
            untilDrop.Restart();
            while (target.Paths.Count == 0 && untilDrop.ElapsedMilliseconds < 1500)
            { app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); Thread.Sleep(10); }
            Assert(target.Paths.SequenceEqual(new[] { fixture }), "Actual routed drop reaches deferred controller commit");
            controller.DropOnDesktop(new(fixture, "Artificial.txt", false), 0, 0);
            Assert(FenceWindow.ReferenceDropEffect(payload, DragDropEffects.Move) == DragDropEffects.None, "Move-only drag never promises a physical move");
            var panel = (System.Windows.Controls.Border)fence.Content;
            var grid = (System.Windows.Controls.Grid)panel.Child;
            var list = grid.Children.OfType<System.Windows.Controls.ListBox>().Single();
            controller.State.Animations = false; fence.SetFolded(true);
            controller.State.Animations = true; fence.SetFolded(false);
            Assert(!SystemParameters.ClientAreaAnimation || System.Windows.Controls.ScrollViewer.GetVerticalScrollBarVisibility(list) == System.Windows.Controls.ScrollBarVisibility.Hidden, "Scrollbar suppressed during expansion");
            controller.State.Animations = false; fence.ApplyAppearance();
            Assert(System.Windows.Controls.ScrollViewer.GetVerticalScrollBarVisibility(list) == System.Windows.Controls.ScrollBarVisibility.Auto, "Animation cancellation restores normal scrolling");
            Assert(fence.ContextMenu == null, "Group background has no extra Kanso context menu");
            File.WriteAllText(fixture, "Artificial menu fixture only");
            using (var nativeMenu = new ShellContextMenu(fixture, IntPtr.Zero)) Assert(nativeMenu.Count > 0, "Windows builds native file context menu without displaying or invoking it");
            var content = (FrameworkElement)fence.Content;
            content.Measure(new Size(410,360)); content.Arrange(new Rect(0,0,410,360)); content.UpdateLayout();
            var image = new RenderTargetBitmap(410,360,96,96,PixelFormats.Pbgra32); image.Render(content);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); using(var file=File.Create(Path.Combine(output,"existing-group-render.png"))) png.Save(file);
            fence.Close();
        }
        string personalRoot = Path.Combine(output, "personal-controller"), desktopRoot = Path.Combine(personalRoot, "desktop"), outsideRoot = Path.Combine(personalRoot,"outside");
        Directory.CreateDirectory(desktopRoot); Directory.CreateDirectory(outsideRoot);
        var originals = new[] { "First.txt", "Second.txt", "Third.txt", "Fourth.txt" }.Select(n => Path.Combine(desktopRoot,n)).ToArray();
        foreach (string path in originals) File.WriteAllText(path, "unaltered");
        string ordinaryFolder = Path.Combine(desktopRoot,"Ordinary folder"); Directory.CreateDirectory(ordinaryFolder);
        string outsideFile = Path.Combine(outsideRoot,"Outside.txt"); File.WriteAllText(outsideFile,"external unchanged");
        var originalAttributes = originals.Select(File.GetAttributes).ToArray();
        var personalState = CopyAppearance(original); new LayoutStore(Path.Combine(personalRoot,"layout.json")).Save(personalState);
        using (var controller = new Controller(personalRoot, new[] { desktopRoot }, false))
        {
            controller.ConfigurePersonalDesktop(99);
            var target = controller.State.Fences.Single(); string[] projected = Array.Empty<string>(); bool accept = true;
            controller.ProjectionForChecks = paths => { if (!accept) return false; projected = paths; return true; };
            Assert(controller.CanAcceptDrop(originals, target), "Personal mode admits ordinary files without session naming");
            Assert(controller.CanAcceptDrop(new[] {ordinaryFolder}, target), "Personal mode admits ordinary folder");
            Assert(!controller.CanAcceptDrop(new[] {"shell:RecycleBinFolder"}, target), "Virtual system object is not a filesystem reference");
            Assert(!NativeReferencePolicy.IsReference("relative.txt") && !NativeReferencePolicy.IsReference("\\\\.\\PhysicalDrive0"), "Relative and device paths rejected");
            Assert(NativeReferencePolicy.IsDesktopChild(originals[0], new[] {desktopRoot}), "Immediate desktop child requires native projection");
            Assert(!NativeReferencePolicy.IsDesktopChild(outsideFile, new[] {desktopRoot}), "External reference does not hide another desktop item");
            Assert(!NativeReferencePolicy.IsDesktopChild(Path.Combine(ordinaryFolder,"Nested.txt"), new[] {desktopRoot}), "Nested file does not hide its parent folder");
            controller.AssignPaths(originals.Append(ordinaryFolder).Append(outsideFile), target);
            Assert(target.Paths.Count == 6 && projected.Length == 5 && !projected.Contains(outsideFile), "More than three ordinary references committed with desktop-only native projection");
            accept = false; controller.DropOnDesktop(new(originals[0],"First.txt",false), 0,0);
            Assert(target.Paths.Contains(originals[0]), "Rejected release preserves existing assignment");
            accept = true; controller.DropOnDesktop(new(originals[0],"First.txt",false), 0,0);
            Assert(!target.Paths.Contains(originals[0]) && !projected.Contains(originals[0]), "Drag-out releases desktop reference");
            controller.StopDesktop();
            Assert(target.Paths.Count == 5, "Stopping personal mode retains assignments");
            controller.FlushAsync().GetAwaiter().GetResult();
            var saved = controller.Store.Load();
            Assert(saved.Fences.Single().Paths.Count == 5 && saved.DesktopPositions.Count == 0, "Persistent references saved without replacement desktop positions");
            Assert(originals.All(p => File.ReadAllText(p) == "unaltered") && originals.Select(File.GetAttributes).SequenceEqual(originalAttributes) && Directory.Exists(ordinaryFolder) && File.ReadAllText(outsideFile) == "external unchanged", "Assignments and releases leave original contents, folder and attributes unchanged");
        }
        using (var restarted = new Controller(personalRoot, new[] {desktopRoot}, false))
        {
            restarted.ConfigurePersonalDesktop(100);
            Assert(restarted.State.Fences.Single().Paths.Count == 5, "Personal references survive a new controller session");
            restarted.StopDesktop();
            Assert(restarted.State.Fences.Single().Paths.Count == 5 && restarted.LooseLayerCount == 0, "Restart/stop creates no replacement desktop and retains references");
        }        using (var controller = new Controller(personalRoot, new[] {desktopRoot}, false))
        {
            controller.ConfigurePersonalDesktop(102);
            int probes = 0, starts = 0; bool ready = false;
            controller.ResumeProbeForChecks = () => { probes++; return ready; };
            controller.ResumeStartForChecks = () => starts++;
            controller.State.DesktopModeRequested = true; controller.State.ResumeDesktop = true;
            var now = DateTime.UtcNow;
            controller.TryResumeDesktop(now);
            Assert(probes == 1 && starts == 0, "Unavailable Explorer is deferred without starting native mode");
            ready = true; controller.TryResumeDesktop(now.AddSeconds(1));
            Assert(probes == 1 && starts == 0, "Startup retries are bounded by backoff");
            controller.TryResumeDesktop(now.AddSeconds(16));
            Assert(starts == 1, "Ready Explorer resumes the requested desktop");
            controller.StopDesktop(); controller.TryResumeDesktop(now.AddSeconds(40));
            Assert(!controller.State.DesktopModeRequested && starts == 1, "Explicit stop cancels pending recovery");
            controller.State.DesktopModeRequested = true; controller.StopDesktop(false);
            Assert(controller.State.DesktopModeRequested, "System recovery retains desired active state");
            controller.State.ResumeDesktop = false; controller.TryResumeDesktop(now.AddSeconds(60));
            Assert(starts == 1, "Disabled restore preference suppresses automatic activation");
            bool registered = false; controller.StartupRegistrationForChecks = enabled => registered = enabled;
            controller.SetWindowsStartup(true); Assert(registered && controller.State.StartWithWindows, "Autostart preference commits after registration succeeds");
            controller.StartupRegistrationForChecks = _ => throw new IOException("injected registration failure");
            try { controller.SetWindowsStartup(false); } catch (IOException) { }
            Assert(controller.State.StartWithWindows, "Failed registration change preserves previous preference");
            Assert(WindowsStartup.Command(@"C:\Program Files\Kanso\Kanso.exe") == "\"C:\\Program Files\\Kanso\\Kanso.exe\" --startup", "Startup executable path is quoted with startup argument");
            bool invalidCommand = false; try { WindowsStartup.Command("relative.exe"); } catch (ArgumentException) { invalidCommand = true; }
            Assert(invalidCommand, "Invalid startup executable path rejected");
            controller.FlushAsync().GetAwaiter().GetResult();
        }
        var resumeLayout = new LayoutStore(Path.Combine(personalRoot,"layout.json")).Load();
        Assert(resumeLayout.DesktopModeRequested && resumeLayout.StartWithWindows && !resumeLayout.ResumeDesktop, "Independent startup and desired mode survive restart");        using (var localized = new Controller(personalRoot, new[] {desktopRoot}, false))
        {
            localized.ConfigurePersonalDesktop(105);
            localized.State.Fences.Single().Title = "Meine Gruppen";
            string originalState = JsonSerializer.Serialize(localized.State);
            foreach (var language in L.Languages)
            {
                localized.SetLanguage(language.Code);
                Assert(L.Current == language.Code && localized.State.Language == language.Code, "Language choice applied: " + language.Code);
                Assert(language.Code == "de" ? L.T("Meine Gruppen") == "Meine Gruppen" : L.T("Meine Gruppen") != "Meine Gruppen", "Main navigation translated: " + language.Code);
                string formatted = L.F("{0} Gruppen  /  {1} bekannte Elemente", 2, 7);
                Assert(formatted.Contains('2') && formatted.Contains('7') && !formatted.Contains("{0}"), "Localized count placeholders preserved: " + language.Code);
                var clone = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(localized.State))!;
                clone.Language = JsonSerializer.Deserialize<Settings>(originalState)!.Language;
                Assert(JsonSerializer.Serialize(clone) == originalState && localized.State.Fences.Single().Title == "Meine Gruppen", "Language switch preserves titles, references and preferences: " + language.Code);
                localized.FlushAsync().GetAwaiter().GetResult();
                Assert(localized.Store.Load().Language == language.Code, "Language survives reload: " + language.Code);
            }
            Assert(L.TranslationCount >= 125 && L.Entries.All(e => e.Value.Length == 6 && e.Value.All(t => !string.IsNullOrWhiteSpace(t))), "Every catalog entry covers all six target languages");
            bool formatsValid = true;
            foreach(var entry in L.Entries) foreach(string code in L.Languages.Select(l=>l.Code))
            { L.Set(code); try { _ = L.F(entry.Key, 2, 7); } catch(FormatException) { formatsValid=false; } }
            Assert(formatsValid, "Every localized format string can be formatted");
            localized.SetLanguage("invalid-language");
            Assert(localized.State.Language == "de" && L.Current == "de", "Invalid language falls back to German");
            Assert(typeof(Controller).Assembly.GetName().Name == "KansoDesktop", "Release assembly has the product name");            var hiddenSettings = new SettingsWindow(localized) { Left = 70, Top = 90, Width = 1060, Height = 800 };
            hiddenSettings.ShowSection(2);
            var settingsField = typeof(Controller).GetField("_settings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            settingsField.SetValue(localized, hiddenSettings);
            localized.SetLanguage("en");
            var refreshed = (SettingsWindow)settingsField.GetValue(localized)!;
            Assert(!refreshed.IsVisible && refreshed.CurrentSection == 2 && refreshed.Left == 70 && refreshed.Top == 90, "Language refresh preserves settings page and geometry without showing a hidden window");
            Assert(!ReferenceEquals(refreshed, hiddenSettings), "Language refresh rebuilds the settings text");
            refreshed.Close(); localized.SetLanguage("de");
        }
        L.Set("de");        File.WriteAllText(Path.Combine(output,"appearance-checks.json"),JsonSerializer.Serialize(new { Checks=count,Passed=true,ExistingUiSourceReused=true,LiveDesktopTouched=false,RealDragTested=false },new JsonSerializerOptions{WriteIndented=true}));
        app.Shutdown();
    }
}








