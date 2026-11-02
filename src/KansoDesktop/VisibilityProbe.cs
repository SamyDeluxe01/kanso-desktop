using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
namespace KansoDesktop;

internal static class VisibilityProbe
{
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr hwnd, DesktopShell.EnumProc callback, IntPtr data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int capacity);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(DesktopShell.NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    internal static void Run(string output)
    {
        var own = new HashSet<uint>(); foreach (var p in Process.GetProcessesByName("KansoDesktop")) { own.Add((uint)p.Id); p.Dispose(); }
        var result = new List<object>(); var seen = new HashSet<IntPtr>();
        void Inspect(IntPtr w)
        {
            if (!seen.Add(w)) return; GetWindowThreadProcessId(w, out var pid); if (!own.Contains(pid)) return;
            var text = new StringBuilder(256); GetWindowText(w, text, text.Capacity); var cls = new StringBuilder(128); DesktopShell.GetClassName(w, cls, cls.Capacity); DesktopShell.GetWindowRect(w, out var r);
            var point = new DesktopShell.NativePoint { X = r.Left + Math.Min(30, Math.Max(1, (r.Right - r.Left) / 2)), Y = r.Top + Math.Min(25, Math.Max(1, (r.Bottom - r.Top) / 2)) }; var hit = WindowFromPoint(point); var hitClass = new StringBuilder(128); DesktopShell.GetClassName(hit, hitClass, hitClass.Capacity);
            var chain = new List<string>(); var previous = GetWindow(w, 3); for (int i = 0; i < 6 && previous != IntPtr.Zero; i++, previous = GetWindow(previous, 3)) { var c = new StringBuilder(128); DesktopShell.GetClassName(previous, c, c.Capacity); chain.Add(c.ToString()); }
            result.Add(new { Handle = w.ToInt64(), PID = pid, Title = text.ToString(), Class = cls.ToString(), Parent = DesktopShell.GetParent(w).ToInt64(), Visible = DesktopShell.IsWindowVisible(w), Rect = new { r.Left, r.Top, r.Right, r.Bottom }, Hit = hit.ToInt64(), HitClass = hitClass.ToString(), Above = chain });
        }
        DesktopShell.EnumWindows((w, _) => { Inspect(w); EnumChildWindows(w, (child, _) => { Inspect(child); return true; }, IntPtr.Zero); return true; }, IntPtr.Zero);
        var monitors = new List<object>(); foreach (var screen in Screen.AllScreens) monitors.Add(new { screen.Primary, screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height });
        File.WriteAllText(output, JsonSerializer.Serialize(new { Windows = result, Monitors = monitors }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
