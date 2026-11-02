using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace KansoDesktop;

internal sealed record NativeIcon(string Path, string Name, int X, int Y);
internal static class DesktopIconLayout
{
    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ServiceProvider { [PreserveSig] int QueryService(ref Guid service, ref Guid iid, out IntPtr result); }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetView(IntPtr self, out IntPtr view);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetCount(IntPtr self, uint flags, out int count);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetItem(IntPtr self, int index, out IntPtr pidl);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetPosition(IntPtr self, IntPtr pidl, out DesktopShell.NativePoint position);
    [DllImport("shell32.dll")] private static extern int SHGetNameFromIDList(IntPtr pidl, uint kind, out IntPtr name);
    private static T Method<T>(IntPtr p, int slot) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(p), slot * IntPtr.Size));
    private static string? Name(IntPtr pidl, uint kind) { if (SHGetNameFromIDList(pidl, kind, out var text) < 0) return null; try { return Marshal.PtrToStringUni(text); } finally { Marshal.FreeCoTaskMem(text); } }
    internal static List<NativeIcon> Read()
    {
        object windows = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"))!)!;
        object location = 0, empty = Type.Missing;
        object desktop = ((dynamic)windows).FindWindowSW(ref location, ref empty, 8, out int hwnd, 1);
        IntPtr browser = IntPtr.Zero, view = IntPtr.Zero, folder = IntPtr.Zero;
        try
        {
            var sid = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837"); var iid = new Guid("000214E2-0000-0000-C000-000000000046");
            Marshal.ThrowExceptionForHR(((ServiceProvider)desktop).QueryService(ref sid, ref iid, out browser));
            Marshal.ThrowExceptionForHR(Method<GetView>(browser, 15)(browser, out view));
            iid = new Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE"); Marshal.ThrowExceptionForHR(Marshal.QueryInterface(view, in iid, out folder));
            Marshal.ThrowExceptionForHR(Method<GetCount>(folder, 7)(folder, 2, out int count));
            if (count > 3000) throw new InvalidOperationException("Zu viele Desktop-Symbole für die aktuelle Beta.");
            var found = DesktopShell.Find(); DesktopShell.GetWindowRect(found.Icons, out var rect);
            var result = new List<NativeIcon>();
            for (int i = 0; i < count; i++)
            {
                Marshal.ThrowExceptionForHR(Method<GetItem>(folder, 6)(folder, i, out var pidl));
                try
                {
                    if (Method<GetPosition>(folder, 11)(folder, pidl, out var pos) < 0) continue;
                    var path = Name(pidl, 0x80058000);
                    if (path == null) { var parsing = Name(pidl, 0x80028000); if (parsing == null) continue; path = "shell:" + parsing; }
                    result.Add(new(path, Name(pidl, 0) ?? System.IO.Path.GetFileName(path), rect.Left + pos.X, rect.Top + pos.Y));
                }
                finally { Marshal.FreeCoTaskMem(pidl); }
            }
            return result;
        }
        finally
        {
            if (folder != IntPtr.Zero) Marshal.Release(folder); if (view != IntPtr.Zero) Marshal.Release(view); if (browser != IntPtr.Zero) Marshal.Release(browser);
            Marshal.ReleaseComObject(desktop); Marshal.ReleaseComObject(windows);
        }
    }
    internal static async Task<List<NativeIcon>> ReadIsolatedAsync(CancellationToken cancellation)
    {
        string file = Path.Combine(Path.GetTempPath(), "kanso-icon-layout-" + Guid.NewGuid().ToString("N") + ".json");
        using var process = DesktopShell.LaunchSelf("--icon-layout", file);
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation); limit.CancelAfter(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(limit.Token);
            if (process.ExitCode != 0 || !File.Exists(file)) throw new IOException("Windows-Symbolpositionen konnten nicht gelesen werden.");
            return JsonSerializer.Deserialize<List<NativeIcon>>(await File.ReadAllTextAsync(file, cancellation)) ?? throw new IOException("Leere Symbolansicht.");
        }
        finally { try { if (!process.HasExited) process.Kill(); } catch { } try { File.Delete(file); } catch { } }
    }
}

