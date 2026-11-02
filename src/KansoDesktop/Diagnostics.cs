using System;
using System.IO;
using System.Threading.Tasks;

namespace KansoDesktop;

internal static class Diagnostics
{
    private static readonly object Gate = new();
    internal static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KansoDesktop", "Logs");
    internal static void Write(string operation, Exception error) => _ = Task.Run(() =>
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder); var path = Path.Combine(Folder, "kanso.log");
                if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) File.Move(path, path + ".previous", true);
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} {operation}\n{error}\n");
            }
        }
        catch { /* Logging must never take down the desktop. */ }
    });
}

