using System;
using System.IO;
using Microsoft.Win32;

namespace KansoDesktop;

internal static class WindowsStartup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal static string Command(string executable)
    {
        if (!Path.IsPathFullyQualified(executable) || executable.Contains('"')) throw new ArgumentException(L.T("Ungültiger Programmpfad."));
        return "\"" + executable + "\" --startup";
    }
    internal static void Set(bool enabled, string executable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true) ?? throw new IOException(L.T("Windows-Autostart konnte nicht geöffnet werden."));
        if (enabled) key.SetValue("KansoDesktop", Command(executable), RegistryValueKind.String);
        else key.DeleteValue("KansoDesktop", false);
    }
}

