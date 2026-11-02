using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KansoDesktop;

// Shell extensions execute only in expendable helper processes, never in the UI process.
internal static class ShellIcons
{
    private static readonly ConcurrentDictionary<string, Task<ImageSource>> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim Slots = new(2);
    internal static ImageSource Fallback { get; } = FromHandle(System.Drawing.SystemIcons.Application.Handle);
    internal static Task<ImageSource> GetAsync(DesktopItem item)
    {
        if (Cache.Count >= 512 && !Cache.ContainsKey(item.Path))
        {
            foreach (var pair in Cache.Where(p => p.Value.IsCompleted).Take(128)) Cache.TryRemove(pair.Key, out _);
            if (Cache.Count >= 512) return Task.FromResult(Fallback);
        }
        return Cache.GetOrAdd(item.Path, _ => Task.Run(() => ReadAsync(item)));
    }
    private static async Task<ImageSource> ReadAsync(DesktopItem item)
    {
        await Slots.WaitAsync().ConfigureAwait(false);
        string output = Path.Combine(Path.GetTempPath(), "kanso-icon-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            using var child = DesktopShell.LaunchSelf("--shell-icon", item.Path, item.Folder ? "folder" : "file", output);
            try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false); }
            catch (TimeoutException) { try { child.Kill(true); } catch { } return Fallback; }
            if (child.ExitCode != 0 || !File.Exists(output)) return Fallback;
            using var input = File.OpenRead(output);
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = input; image.EndInit(); image.Freeze(); return image;
        }
        catch (Exception ex) { Diagnostics.Write("Isoliertes Shell-Symbol", ex); return Fallback; }
        finally { try { File.Delete(output); } catch { } Slots.Release(); }
    }
    internal static void Extract(string path, bool folder, string output)
    {
        int initialized = OleInitialize(IntPtr.Zero);
        try
        {
            bool generic = false;
            if (!path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            {
                try { var attr = File.GetAttributes(path); generic = (attr & (FileAttributes.Offline | (FileAttributes)0x400000 | (FileAttributes)0x40000)) != 0; } catch { generic = true; }
            }
            ShellInfo info;
            if (path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            {
                if (SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, out _) < 0 || pidl == IntPtr.Zero) throw new IOException("Systemsymbol nicht verfügbar.");
                try { if (SHGetFileInfo(pidl, 0, out info, (uint)Marshal.SizeOf<ShellInfo>(), 0x108) == IntPtr.Zero) throw new IOException("Systemsymbol nicht verfügbar."); }
                finally { Marshal.FreeCoTaskMem(pidl); }
            }
            else
            {
                string target = generic ? (folder ? "folder" : "file" + Path.GetExtension(path)) : path;
                uint flags = 0x100u | (generic ? 0x10u : 0u);
                if (SHGetFileInfo(target, folder ? 0x10u : 0x80u, out info, (uint)Marshal.SizeOf<ShellInfo>(), flags) == IntPtr.Zero) throw new IOException("Kein Shell-Symbol verfügbar.");
            }
            if (info.Icon == IntPtr.Zero) throw new IOException("Kein Shell-Symbol verfügbar.");
            try { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create((BitmapSource)FromHandle(info.Icon))); using var file = File.Create(output); encoder.Save(file); }
            finally { DestroyIcon(info.Icon); }
        }
        finally { if (initialized >= 0) OleUninitialize(); }
    }
    private static ImageSource FromHandle(IntPtr handle) { var image = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()); image.Freeze(); return image; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct ShellInfo { public IntPtr Icon; public int Index; public uint Attributes; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string Type; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SHGetFileInfo(string path, uint attr, out ShellInfo info, uint size, uint flags);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SHGetFileInfo(IntPtr path, uint attr, out ShellInfo info, uint size, uint flags);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHParseDisplayName(string path, IntPtr context, out IntPtr pidl, uint attributes, out uint result);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("ole32.dll")] private static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")] private static extern void OleUninitialize();
}

