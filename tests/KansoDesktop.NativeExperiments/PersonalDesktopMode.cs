using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace KansoDesktop;

internal static class NativeReferencePolicy
{
    internal static bool IsReference(string path)
    {
        try { return !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && !path.StartsWith("\\\\?\\", StringComparison.Ordinal) && !path.StartsWith("\\\\.\\", StringComparison.Ordinal); }
        catch (ArgumentException) { return false; }
    }
    internal static bool IsDesktopChild(string path, string[] roots)
    {
        if (!IsReference(path)) return false;
        string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
        return roots.Any(r => !string.IsNullOrWhiteSpace(r) && string.Equals(parent, Path.TrimEndingDirectorySeparator(Path.GetFullPath(r)), StringComparison.OrdinalIgnoreCase));
    }
}

internal sealed partial class Controller
{
    internal bool NativePersonalEnabled { get; private set; }
    internal void ConfigurePersonalDesktop(ulong token)
    {
        NativePersonalEnabled = true;
        ConfigureNativePreview(token, Array.Empty<string>());
    }
    private readonly DispatcherTimer _resumeTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private DateTime _nextResumeAttempt;
    internal Func<bool>? ResumeProbeForChecks;
    internal Action? ResumeStartForChecks;
    internal Action<bool>? StartupRegistrationForChecks;
    internal void SetWindowsStartup(bool enabled)
    {
        if (StartupRegistrationForChecks != null) StartupRegistrationForChecks(enabled);
        else WindowsStartup.Set(enabled, Environment.ProcessPath ?? throw new IOException(L.T("Programmpfad fehlt.")));
        State.StartWithWindows = enabled; Save();
    }
    internal void ConfigureAutoResume()
    {
        _resumeTimer.Tick += (_, _) => TryResumeDesktop(DateTime.UtcNow);
        _resumeTimer.Start();
        TryResumeDesktop(DateTime.UtcNow);
    }
    internal void TryResumeDesktop(DateTime now)
    {
        if (_disposed || _exiting || !NativePersonalEnabled || !State.ResumeDesktop || !State.DesktopModeRequested || DesktopMode || _startingDesktop || now < _nextResumeAttempt) return;
        _nextResumeAttempt = now.AddSeconds(15);
        bool ready;
        try { var found = ResumeProbeForChecks == null ? DesktopShell.Find() : default; ready = ResumeProbeForChecks?.Invoke() ?? (found.Host != IntPtr.Zero && DesktopShell.IsWindowVisible(found.Icons)); }
        catch { ready = false; }
        if (!ready) return;
        if (ResumeStartForChecks != null) ResumeStartForChecks(); else StartPersonalDesktop();
    }
    private async void StartPersonalDesktop()
    {
        if (DesktopMode || _startingDesktop) return;
        State.DesktopModeRequested = true; Save();
        _startingDesktop = true;
        int generation = ++_desktopGeneration;
        try
        {
            await ScanAsync();
            if (_disposed || generation != _desktopGeneration) return;
            var found = DesktopShell.Find();
            if (found.Host == IntPtr.Zero || !DesktopShell.IsWindowVisible(found.Icons))
                throw new InvalidOperationException(L.T("Die vorhandenen Windows-Desktopsymbole müssen sichtbar sein."));
            var connection = new ArtificialDesktopConnection(_previewToken, true);
            _previewConnection = connection;
            connection.Connect();
            Host = found.Host; _icons = found.Icons; _wasVisible = false;
            DesktopMode = true; _monitorLayout = MonitorLayout();
            if (!PreviewProject(State)) return;
            Rebuild(); _previewHeartbeat.Start();
            Report(L.T("Eigene Dateien und Ordner können jetzt zugeordnet werden. Die Originale bleiben am Speicherort."));
        }
        catch (Exception ex) { StopDesktop(false); Report(L.T("Desktop-Modus konnte nicht starten: ") + ex.Message, ex); }
        finally { _startingDesktop = false; StatusChanged?.Invoke(); }
    }
}

internal static class PersonalDesktopMode
{
    internal static void Run(string root, bool startup = false)
    {
        using var mutex = new Mutex(false, "Local\\KansoDesktopBeta"); bool owns;
        try { owns = mutex.WaitOne(0); } catch (AbandonedMutexException) { owns = true; }
        if (!owns) { if (!startup) MessageBox.Show(L.T("Kanso läuft bereits. Bitte die bisherige Sitzung regulär beenden.")); return; }
        try
        {
            Directory.CreateDirectory(root);
            var store = new LayoutStore(Path.Combine(root, "layout.json"));
            if (!File.Exists(store.FileName))
            {
                var prior = new LayoutStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KansoDesktop", "layout.json")).Load();
                // Preserve appearance, but never silently activate historical assignments.
                store.Save(NativeAppearancePreview.CopyAppearance(prior));
            }
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            using var controller = new Controller(root);
            ulong token = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)) & 0x7FFFFFFFFFFFFFFFUL;
            controller.ConfigurePersonalDesktop(token == 0 ? 1 : token);
            app.SessionEnding += (_, _) =>
            {
                controller.StopDesktop(false);
                try { controller.FlushAsync().Wait(TimeSpan.FromSeconds(2)); }
                catch (Exception ex) { Diagnostics.Write("Zuordnungen beim Abmelden speichern", ex); }
            };
            app.DispatcherUnhandledException += (_, e) => { controller.StopDesktop(false); MessageBox.Show(L.T("Desktop-Modus beendet: ") + e.Exception.Message); e.Handled = true; app.Shutdown(1); };
            bool startupConfigured = true;
            try { controller.SetWindowsStartup(controller.State.StartWithWindows); }
            catch (Exception ex) { startupConfigured = false; controller.Report(L.T("Windows-Autostart konnte nicht eingerichtet werden."), ex); controller.ShowSettings(); }
            if (!startup) controller.ShowSettings();
            app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, controller.ConfigureAutoResume);
            if (startupConfigured) controller.Report(L.T("Kanso stellt den zuletzt aktiven Desktop-Modus automatisch wieder her. Autostart und Wiederherstellung sind in den Einstellungen schaltbar."));
            app.Run();
        }
        finally { mutex.ReleaseMutex(); }
    }
}



