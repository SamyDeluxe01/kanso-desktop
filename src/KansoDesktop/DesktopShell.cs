using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace KansoDesktop;

internal static class DesktopShell
{
    internal delegate bool EnumProc(IntPtr hwnd, IntPtr data);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumProc callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll")] internal static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SetParent(IntPtr hwnd, IntPtr parent);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] internal static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] internal static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] internal static extern bool ScreenToClient(IntPtr hwnd, ref NativePoint point);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr CreateWindowEx(int ex, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr data);
    [DllImport("user32.dll")] internal static extern bool DestroyWindow(IntPtr hwnd);
    [StructLayout(LayoutKind.Sequential)] internal struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct NativePoint { public int X, Y; }

    internal static (IntPtr Host, IntPtr Icons) Find()
    {
        IntPtr host = IntPtr.Zero, icons = IntPtr.Zero;
        EnumWindows((w, _) =>
        {
            var view = FindWindowEx(w, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (view == IntPtr.Zero) return true;
            var list = FindWindowEx(view, IntPtr.Zero, "SysListView32", "FolderView");
            if (list == IntPtr.Zero) return true;
            host = w; icons = list; return false;
        }, IntPtr.Zero);
        return (host, icons);
    }

    internal static void Restore(IntPtr icons)
    {
        if (!IsWindow(icons)) return;
        var cls = new StringBuilder(128); GetClassName(icons, cls, cls.Capacity);
        if (cls.ToString() == "SysListView32") ShowWindow(icons, 5);
    }

    internal static void Watch(string[] args)
    {
        if (args.Length != 4 || !int.TryParse(args[1], out var pid) || !long.TryParse(args[2], out var handle) || !long.TryParse(args[3], out var start)) return;
        // Never guess a replacement desktop window: restore only the original view.
        try { using var p = Process.GetProcessById(pid); if (p.StartTime.ToUniversalTime().Ticks != start) return; p.WaitForExit(); }
        catch (ArgumentException) { }
        catch { return; }
        Restore(new IntPtr(handle));
    }

    internal static Process StartWatchdog(IntPtr icons)
    {
        using var self = Process.GetCurrentProcess();
        return LaunchSelf("--watchdog", self.Id.ToString(), icons.ToInt64().ToString(), self.StartTime.ToUniversalTime().Ticks.ToString());
    }

    internal static Process LaunchSelf(params string[] arguments)
    {
        var exe = Environment.ProcessPath ?? throw new IOException("App-Pfad fehlt.");
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "KansoDesktop.dll"));
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        return Process.Start(start) ?? throw new IOException("Hilfsprozess konnte nicht starten.");
    }

    [DllImport("user32.dll")] internal static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll")] private static extern IntPtr GetTopWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flag);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    internal static bool IsDesktopBackground(int x, int y)
    {
        var hit = WindowFromPoint(new NativePoint { X = x, Y = y }); if (hit == IntPtr.Zero) return false;
        var root = GetAncestor(hit, 2);
        if (LooseDesktopWindows.Contains(root)) return true;
        var cls = new StringBuilder(128); GetClassName(root, cls, cls.Capacity);
        if (cls.ToString() != "WorkerW" && cls.ToString() != "Progman") return false;
        var desktop = Find(); if (desktop.Host == IntPtr.Zero) return false;
        GetWindowThreadProcessId(root, out var hitPid); GetWindowThreadProcessId(desktop.Host, out var shellPid); return hitPid == shellPid;
    }
    private static readonly HashSet<IntPtr> DesktopWindows = new();
    private static readonly HashSet<IntPtr> LooseDesktopWindows = new();
    internal static void Attach(Window window, IntPtr host, double x, double y)
    {
        var interop = new WindowInteropHelper(window); var handle = interop.Handle;
        if ((GetWindowLong(handle, -16) & 0x40000000) != 0) throw new InvalidOperationException("Ein altes Unterfenster kann nicht als transparente Desktop-Fläche weiterverwendet werden.");
        // Keep WPF's layered renderer top-level. Ownership keeps it above the desktop;
        // it is deliberately neither a child window nor always-on-top.
        interop.Owner = host;
        if (GetWindow(handle, 4) != host) throw new InvalidOperationException("Desktop-Eigentümer konnte nicht gesetzt werden.");
        DesktopWindows.Add(handle);
        if (window is DesktopIconsWindow) LooseDesktopWindows.Add(handle);
        window.Closed += (_, _) => { DesktopWindows.Remove(handle); LooseDesktopWindows.Remove(handle); };
        Position(window, host, x, y);
        SetWindowPos(handle, new IntPtr(1), 0, 0, 0, 0, 0x4213); // bottom, no move/size/activation/owner reorder
        if (window is FenceWindow) RaiseAboveIconLayers(handle);
    }
    private static void RaiseAboveIconLayers(IntPtr handle)
    {
        var current = GetTopWindow(IntPtr.Zero);
        for (int i = 0; i < 10000 && current != IntPtr.Zero; i++, current = GetWindow(current, 2))
        {
            if (current == handle || !DesktopWindows.Contains(current)) continue;
            var previous = GetWindow(current, 3);
            if (previous != handle) SetWindowPos(handle, previous, 0, 0, 0, 0, 0x4213);
            return;
        }
    }
    internal static void Position(Window window, IntPtr host, double x, double y)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var scale = GetDpiForWindow(hwnd) / 96.0; if (scale <= 0) scale = 1;
        int px = (int)Math.Round(x), py = (int)Math.Round(y), width = (int)Math.Round(window.Width * scale), height = (int)Math.Round(window.Height * scale);
        if (GetWindowRect(hwnd, out var rect) && rect.Left == px && rect.Top == py && rect.Right - rect.Left == width && rect.Bottom - rect.Top == height) return;
        SetWindowPos(hwnd, IntPtr.Zero, px, py, width, height, 0x4214);
    }
}

