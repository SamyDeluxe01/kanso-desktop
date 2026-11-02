using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace KansoDesktop;

// Isolated development tool, deliberately excluded from the production app.
// Read-only capability negotiation. Never sets filters, removes Shell items,
// changes positions or hides any window. Runs in a bounded helper process.
internal static class NativeDesktopCapabilities
{
    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ServiceProvider { [PreserveSig] int QueryService(ref Guid service, ref Guid iid, out IntPtr result); }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetView(IntPtr self, out IntPtr view);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetFolder(IntPtr self, ref Guid iid, out IntPtr folder);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetSite(IntPtr self, ref Guid iid, out IntPtr site);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetItemObject(IntPtr self, uint flags, ref Guid iid, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetWindow(IntPtr self, out IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string? name, string? title);
    [DllImport("user32.dll")] private static extern int GetWindowLongW(IntPtr window, int index);

    internal static Dictionary<string, string> Read()
    {
        var result = new Dictionary<string, string> { ["WindowsVersion"] = Environment.OSVersion.Version.ToString(), ["Operation"] = "QueryInterface only; no desktop mutation" };
        object? windows = null, desktop = null;
        IntPtr browser = IntPtr.Zero, view = IntPtr.Zero, document = IntPtr.Zero;
        try
        {
            windows = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"))!)!;
            object location = 0, empty = Type.Missing;
            desktop = ((dynamic)windows).FindWindowSW(ref location, ref empty, 8, out int hwnd, 1);
            var sid = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
            var iid = new Guid("000214E2-0000-0000-C000-000000000046");
            Marshal.ThrowExceptionForHR(((ServiceProvider)desktop).QueryService(ref sid, ref iid, out browser));
            var method = Marshal.GetDelegateForFunctionPointer<GetView>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(browser), 15 * IntPtr.Size));
            Marshal.ThrowExceptionForHR(method(browser, out view));
            var getWindow = Marshal.GetDelegateForFunctionPointer<GetWindow>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(view), 3 * IntPtr.Size));
            if (getWindow(view, out var shellWindow) >= 0)
            {
                var list = FindWindowExW(shellWindow, IntPtr.Zero, "SysListView32", null);
                result["DesktopNativeListPresent"] = (list != IntPtr.Zero).ToString();
                if (list != IntPtr.Zero) result["DesktopNativeListOwnerData"] = ((GetWindowLongW(list, -16) & 0x1000) != 0).ToString();
            }
            document = Marshal.GetIUnknownForObject(desktop);
            iid = new Guid("29EC8E6C-46D3-411F-BAAA-611A6C9CAC66");
            var getBackground = Marshal.GetDelegateForFunctionPointer<GetItemObject>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(view), 15 * IntPtr.Size));
            int backgroundHr = getBackground(view, 0, ref iid, out var background);
            result["ShellView.Background.IShellFolderViewDual3"] = $"0x{backgroundHr:X8}";
            if (background != IntPtr.Zero) Marshal.Release(background);
            iid = new Guid("00020400-0000-0000-C000-000000000046");
            backgroundHr = getBackground(view, 0, ref iid, out background);
            result["ShellView.Background.IDispatch"] = $"0x{backgroundHr:X8}";
            if (background != IntPtr.Zero)
            {
                try { Probe("ShellView.BackgroundDispatch.IShellFolderViewDual3", background, "29EC8E6C-46D3-411F-BAAA-611A6C9CAC66"); }
                finally { Marshal.Release(background); }
            }
            foreach (var (name, pointer) in new[] { ("ShellView", view), ("ShellBrowser", browser), ("DesktopDocument", document) })
            {
                Probe(name + ".IFolderFilterSite", pointer, "C0A651F5-B48B-11D2-B5ED-006097C686F6");
                Probe(name + ".IShellFolderView", pointer, "37A378C0-F82D-11CE-AE65-08002B2E1262");
                Probe(name + ".IFolderView2", pointer, "1AF3A467-214F-4298-908E-06B03E0B39F9");
                Probe(name + ".IObjectWithSite", pointer, "FC4801A3-2BA9-11CF-A229-00AA003D7352");
                Probe(name + ".ICommDlgBrowser", pointer, "000214F1-0000-0000-C000-000000000046");
            }
            iid = new Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE");
            if (Marshal.QueryInterface(view, in iid, out var folderView) >= 0)
            {
                try
                {
                    iid = new Guid("000214E6-0000-0000-C000-000000000046");
                    var getFolder = Marshal.GetDelegateForFunctionPointer<GetFolder>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(folderView), 5 * IntPtr.Size));
                    int hr = getFolder(folderView, ref iid, out var folder);
                    result["DesktopFolder.GetFolder"] = $"0x{hr:X8}";
                    if (folder != IntPtr.Zero)
                    {
                        try { Probe("DesktopFolder.IFolderFilterSite", folder, "C0A651F5-B48B-11D2-B5ED-006097C686F6"); }
                        finally { Marshal.Release(folder); }
                    }
                }
                finally { Marshal.Release(folderView); }
            }
            iid = new Guid("FC4801A3-2BA9-11CF-A229-00AA003D7352");
            if (Marshal.QueryInterface(view, in iid, out var withSite) >= 0)
            {
                try
                {
                    iid = new Guid("00000000-0000-0000-C000-000000000046");
                    var getSite = Marshal.GetDelegateForFunctionPointer<GetSite>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(withSite), 4 * IntPtr.Size));
                    int hr = getSite(withSite, ref iid, out var site);
                    result["ShellView.GetSite"] = $"0x{hr:X8}";
                    if (site != IntPtr.Zero)
                    {
                        try { Probe("ShellViewSite.IFolderFilterSite", site, "C0A651F5-B48B-11D2-B5ED-006097C686F6"); Probe("ShellViewSite.ICommDlgBrowser", site, "000214F1-0000-0000-C000-000000000046"); }
                        finally { Marshal.Release(site); }
                    }
                }
                finally { Marshal.Release(withSite); }
            }
            return result;
        }
        finally
        {
            if (document != IntPtr.Zero) Marshal.Release(document);
            if (view != IntPtr.Zero) Marshal.Release(view);
            if (browser != IntPtr.Zero) Marshal.Release(browser);
            if (desktop != null && Marshal.IsComObject(desktop)) Marshal.ReleaseComObject(desktop);
            if (windows != null && Marshal.IsComObject(windows)) Marshal.ReleaseComObject(windows);
        }
        void Probe(string key, IntPtr pointer, string id)
        {
            var iid = new Guid(id);
            int hr = Marshal.QueryInterface(pointer, in iid, out var queried);
            result[key] = $"0x{hr:X8}";
            if (queried != IntPtr.Zero) Marshal.Release(queried);
        }
    }
}
