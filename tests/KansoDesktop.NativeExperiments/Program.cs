using System;
using System.IO;
using System.Text.Json;

namespace KansoDesktop;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--startup") { PersonalDesktopMode.Run(Path.Combine(AppContext.BaseDirectory, "user-state"), true); return 0; }
        // Opening the separate executable only shows the explanatory start screen.
        // Desktop files and native attachment require its explicit Start action.
        if (args.Length == 0) args = new[] { "native-personal", Path.Combine(AppContext.BaseDirectory, "user-state") };
        if (args.Length == 0) args = new[] { "native-preview", Path.Combine(AppContext.BaseDirectory, "sessions", DateTime.Now.ToString("yyyyMMdd-HHmmss")) };
        if (args.Length == 4 && args[0] == "--shell-icon") { ShellIcons.Extract(args[1], args[2] == "folder", args[3]); return 0; }
        if (args.Length != 2 || (args[0] != "capabilities" && args[0] != "shell-view" && args[0] != "projection" && args[0] != "shell-projection" && args[0] != "bridge-check" && args[0] != "bridge-owner" && args[0] != "bridge-fixture" && args[0] != "desktop-observe" && args[0] != "desktop-lab" && args[0] != "lab-checks" && args[0] != "native-preview" && args[0] != "native-personal" && args[0] != "appearance-checks")) return 2;
        string output = Path.GetFullPath(args[1]);
        try
        {
            if (args[0] == "capabilities")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.WriteAllText(output, JsonSerializer.Serialize(NativeDesktopCapabilities.Read(), new JsonSerializerOptions { WriteIndented = true }));
            }
            else if (args[0] == "projection") NativeProjectionFixture.Check(output);
            else if (args[0] == "shell-projection") NativeShellView.CheckDesktopProjection(output);
            else if (args[0] == "bridge-check") NativeBridgeChecks.Check(output);
            else if (args[0] == "bridge-owner") NativeBridgeChecks.RunOwner(output);
            else if (args[0] == "bridge-fixture") NativeBridgeChecks.RunFixture(output);
            else if (args[0] == "native-personal") PersonalDesktopMode.Run(output);
            else if (args[0] == "native-preview") NativeAppearancePreview.Run(output);
            else if (args[0] == "appearance-checks") NativeAppearancePreview.Check(output);
            else if (args[0] == "desktop-lab") ArtificialDesktopLab.Run(output);
            else if (args[0] == "lab-checks") ArtificialDesktopLab.Check(output);
            else if (args[0] == "desktop-observe") NativeBridgeChecks.ObserveDesktop(output);
            else NativeShellView.Check(output);
            return 0;
        }
        catch (Exception ex)
        {
            string error = args[0] == "capabilities" ? output : args[0].StartsWith("bridge-") && args[0] != "bridge-check" ? output + ".error.txt" : Path.Combine(output, "shell-view-error.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(error)!);
            File.WriteAllText(error, ex.ToString());
            return 1;
        }
    }
}





