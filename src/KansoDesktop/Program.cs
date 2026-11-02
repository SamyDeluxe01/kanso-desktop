using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace KansoDesktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--drag-checks") { try { Smoke.DragChecks(args[1]); Environment.Exit(0); } catch (Exception ex) { Directory.CreateDirectory(args[1]); File.WriteAllText(Path.Combine(args[1], "drag-error.txt"), ex.ToString()); Environment.Exit(1); } return; }
        if (args.Length == 4 && args[0] == "--shell-icon") { try { ShellIcons.Extract(args[1], args[2] == "folder", args[3]); Environment.Exit(0); } catch { Environment.Exit(1); } return; }
        if (args.Length == 2 && args[0] == "--visibility-probe") { VisibilityProbe.Run(args[1]); return; }
        if (args.Length == 2 && args[0] == "--icon-layout")
        {
            try { File.WriteAllText(args[1], System.Text.Json.JsonSerializer.Serialize(DesktopIconLayout.Read())); Environment.Exit(0); }
            catch (Exception ex) { Diagnostics.Write("Desktop-Symbolpositionen", ex); Environment.Exit(1); }
            return;
        }
        if (args.Length == 1 && args[0] == "--probe-lifetime") { Thread.Sleep(1500); return; }
        if (args.Length == 2 && args[0] == "--visual-probe") { try { Smoke.DesktopProbe(args[1], true); Environment.Exit(0); } catch (Exception ex) { Directory.CreateDirectory(args[1]); File.WriteAllText(Path.Combine(args[1], "desktop-error.txt"), ex.ToString()); Environment.Exit(1); } return; }
        if (args.Length == 2 && args[0] == "--desktop-probe")
        {
            try { Smoke.DesktopProbe(args[1]); Environment.Exit(0); }
            catch (Exception ex) { Directory.CreateDirectory(args[1]); File.WriteAllText(Path.Combine(args[1], "desktop-error.txt"), ex.ToString()); Environment.Exit(1); }
            return;
        }
        if (args.Length == 2 && args[0] == "--smoke")
        {
            try { Smoke.Run(args[1]); Environment.Exit(0); }
            catch (Exception ex) { Directory.CreateDirectory(args[1]); File.WriteAllText(Path.Combine(args[1], "smoke-error.txt"), ex.ToString()); Environment.Exit(1); }
            return;
        }
        if (args.Length > 0 && args[0] == "--watchdog") { DesktopShell.Watch(args); return; }
        if (args.Length > 0 && args[0] == "--restore-desktop") { DesktopShell.Restore(DesktopShell.Find().Icons); return; }
        using var mutex = new Mutex(true, "Local\\KansoDesktopBeta", out var first);
        if (!first) { MessageBox.Show("Kanso Desktop läuft bereits. Öffne es über das Symbol im Infobereich."); return; }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        using var health = new UiHealthMonitor();
        TaskScheduler.UnobservedTaskException += (_, e) => { Diagnostics.Write("Hintergrundaufgabe", e.Exception); e.SetObserved(); };
        Controller? controller = null;
        app.DispatcherUnhandledException += (_, e) =>
        {
            Diagnostics.Write("Unbehandelte Oberfläche", e.Exception);
            controller?.RestoreDesktop();
            MessageBox.Show("Kanso wurde sicher angehalten. Die Windows-Symbole wurden wieder eingeblendet.\n\n" + e.Exception.Message, "Kanso Desktop");
            e.Handled = true; app.Shutdown(1);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, _) => controller?.RestoreDesktop();
        app.Exit += (_, _) => controller?.Dispose();
        try { controller = new Controller(); controller.ShowSettings(); app.Run(); }
        catch (Exception ex) { controller?.Dispose(); MessageBox.Show("Kanso konnte nicht starten. Bestehende Dateien bleiben unverändert.\n\n" + ex.Message); }
    }
}




