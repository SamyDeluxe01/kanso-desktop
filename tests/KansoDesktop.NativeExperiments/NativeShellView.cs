using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Forms = System.Windows.Forms;

namespace KansoDesktop;

// Isolated development project; not compiled into the production application.
// Windows' own Shell control. Development probe only: not connected to the live
// desktop or user layout until filtering, input and recovery are validated.
internal sealed class NativeShellView : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct FolderSettings { public uint ViewMode, Flags; }
    [ComImport, Guid("DFD3B6B5-C10C-4BE9-85F6-A66969F402F6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ExplorerBrowser
    {
        [PreserveSig] int Initialize(IntPtr parent, ref Rect rect, ref FolderSettings settings);
        [PreserveSig] int Destroy();
        [PreserveSig] int SetRect(IntPtr deferred, Rect rect);
        [PreserveSig] int SetPropertyBag([MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int SetEmptyText([MarshalAs(UnmanagedType.LPWStr)] string text);
        [PreserveSig] int SetFolderSettings(ref FolderSettings settings);
        [PreserveSig] int Advise(IntPtr events, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetOptions(uint options);
        [PreserveSig] int GetOptions(out uint options);
        [PreserveSig] int BrowseToIDList(IntPtr pidl, uint flags);
        [PreserveSig] int BrowseToObject([MarshalAs(UnmanagedType.IUnknown)] object target, uint flags);
        [PreserveSig] int FillFromObject([MarshalAs(UnmanagedType.IUnknown)] object target, uint flags);
        [PreserveSig] int RemoveAll();
        [PreserveSig] int GetCurrentView(ref Guid iid, out IntPtr view);
    }
    [ComImport, Guid("C0A651F5-B48B-11D2-B5ED-006097C686F6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface FilterSite { [PreserveSig] int SetFilter([MarshalAs(UnmanagedType.IUnknown)] object? filter); }
    [ComImport, Guid("3CC974D2-B302-4D36-AD3E-06D93F695D3F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface FolderViewOptions
    {
        [PreserveSig] int SetFolderViewOptions(uint mask, uint flags);
        [PreserveSig] int GetFolderViewOptions(out uint flags);
    }
    [ComImport, Guid("FC4801A3-2BA9-11CF-A229-00AA003D7352"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ObjectWithSite
    {
        [PreserveSig] int SetSite([MarshalAs(UnmanagedType.IUnknown)] object? site);
        [PreserveSig] int GetSite(ref Guid iid, out IntPtr site);
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetCount(IntPtr self, uint flags, out int count);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetChild(IntPtr self, int index, out IntPtr pidl);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetFocused(IntPtr self, out int index);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetWindow(IntPtr self, out IntPtr window);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int RefreshView(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SelectItem(IntPtr self, int index, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetItemObject(IntPtr self, uint which, ref Guid iid, out IntPtr item);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int QueryMenu(IntPtr self, IntPtr menu, uint index, uint first, uint last, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int FilterText(IntPtr self, [MarshalAs(UnmanagedType.BStr)] string text);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)] private delegate int GetCommandString(IntPtr self, UIntPtr command, uint type, IntPtr reserved, [Out] StringBuilder text, uint count);
    private readonly object _com;
    private readonly ExplorerBrowser _browser;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private bool _initialized;
    private readonly NativeShellFilter _filter = new();
    private bool _filterAttached;
    internal NativeShellView(IntPtr parent, Rect rect, bool legacyList = false)
    {
        if (System.Threading.Thread.CurrentThread.GetApartmentState() != System.Threading.ApartmentState.STA) throw new InvalidOperationException("Native Shell views require their owning STA thread.");
        _com = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("71F96385-DDD6-48D3-A0C1-AE06E8B055FB"))!)!;
        _browser = (ExplorerBrowser)_com;
        try
        {
            // No persisted Explorer preferences, travel state or navigation frame.
            Marshal.ThrowExceptionForHR(_browser.SetOptions(0x008 | 0x040 | 0x080)); // NOTRAVELLOG | NOBORDER | NOPERSISTVIEWSTATE
            if (legacyList) Marshal.ThrowExceptionForHR(((FolderViewOptions)_com).SetFolderViewOptions(1, 1));
            var settings = new FolderSettings { ViewMode = 1, Flags = 0 };
            Marshal.ThrowExceptionForHR(_browser.Initialize(parent, ref rect, ref settings));
            _initialized = true;
        }
        catch { try { _browser.Destroy(); } catch { } Marshal.ReleaseComObject(_com); throw; }
    }
    internal void SetExcludedPaths(IEnumerable<string> paths)
    {
        SetPaths(paths, false);
    }
    internal void SetIncludedPaths(IEnumerable<string> paths)
    {
        SetPaths(paths, true);
    }
    private void SetPaths(IEnumerable<string> paths, bool includeOnly)
    {
        EnsureAccess();
        _filter.SetPaths(paths, includeOnly);
        if (!_filterAttached)
        {
            Marshal.ThrowExceptionForHR(((FilterSite)_com).SetFilter(_filter));
            _filterAttached = true;
        }
        var iid = new Guid("000214E3-0000-0000-C000-000000000046"); // IShellView
        if (_browser.GetCurrentView(ref iid, out var view) >= 0)
        {
            try { Marshal.ThrowExceptionForHR(Method<RefreshView>(view, 8)(view)); }
            finally { Marshal.Release(view); }
        }
    }
    internal HashSet<string> SelectedCanonicalVerbs()
    {
        EnsureAccess();
        var iid = new Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE");
        Marshal.ThrowExceptionForHR(_browser.GetCurrentView(ref iid, out var folder));
        try { Marshal.ThrowExceptionForHR(Method<SelectItem>(folder, 15)(folder, 0, 0x15)); }
        finally { Marshal.Release(folder); }
        iid = new Guid("000214E3-0000-0000-C000-000000000046");
        Marshal.ThrowExceptionForHR(_browser.GetCurrentView(ref iid, out var view));
        IntPtr context = IntPtr.Zero, menu = IntPtr.Zero;
        try
        {
            iid = new Guid("000214E4-0000-0000-C000-000000000046");
            Marshal.ThrowExceptionForHR(Method<GetItemObject>(view, 15)(view, 1, ref iid, out context));
            menu = CreatePopupMenu();
            if (menu == IntPtr.Zero) throw new InvalidOperationException("Native menu allocation failed.");
            int result = Method<QueryMenu>(context, 3)(context, menu, 0, 1, 0x7FFF, 0x10 /* CMF_CANRENAME */);
            Marshal.ThrowExceptionForHR(result);
            var verbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (uint index = 0; index < (result & 0xFFFF); index++)
            {
                var text = new StringBuilder(260);
                if (Method<GetCommandString>(context, 5)(context, new UIntPtr(index), 4, IntPtr.Zero, text, 260) >= 0 && text.Length > 0) verbs.Add(text.ToString());
            }
            return verbs;
        }
        finally
        {
            if (menu != IntPtr.Zero) DestroyMenu(menu);
            if (context != IntPtr.Zero) Marshal.Release(context);
            Marshal.Release(view);
        }
    }
    private static T Method<T>(IntPtr instance, int slot) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));
    // Probe only on this owned fixture view. Never obtains or changes the live desktop.
    private Dictionary<string, object> CheckAutomationFilter()
    {
        EnsureAccess();
        var results = new Dictionary<string, object>();
        var iid = new Guid("000214E3-0000-0000-C000-000000000046");
        Marshal.ThrowExceptionForHR(_browser.GetCurrentView(ref iid, out var view));
        IntPtr automation = IntPtr.Zero;
        try
        {
            iid = new Guid("00020400-0000-0000-C000-000000000046");
            int hr = Method<GetItemObject>(view, 15)(view, 0, ref iid, out var dispatch);
            results["BackgroundDispatch"] = $"0x{hr:X8}";
            if (hr >= 0)
            {
                try { iid = new Guid("29EC8E6C-46D3-411F-BAAA-611A6C9CAC66"); hr = Marshal.QueryInterface(dispatch, in iid, out automation); }
                finally { Marshal.Release(dispatch); }
            }
            results["Interface"] = $"0x{hr:X8}";
            if (hr < 0) return results;
            var filter = Method<FilterText>(automation, 27);
            foreach (string expression in new[] { "Alpha", "Alpha.txt", "*.txt", "NOT System.FileName:=\"Alpha.txt\"", "!Alpha.txt", "Beta.txt;Folder", "" })
            {
                hr = filter(automation, expression);
                var timer = Stopwatch.StartNew();
                while (timer.ElapsedMilliseconds < 500) { Forms.Application.DoEvents(); System.Threading.Thread.Sleep(10); }
                results[expression.Length == 0 ? "Reset" : expression] = new { HResult = $"0x{hr:X8}", Count = ItemCount() };
            }
        }
        finally
        {
            if (automation != IntPtr.Zero)
            {
                Method<FilterText>(automation, 27)(automation, "");
                Marshal.Release(automation);
            }
            Marshal.Release(view);
        }
        return results;
    }
    internal string FilterCapability()
    {
        EnsureAccess();
        IntPtr unknown = Marshal.GetIUnknownForObject(_com);
        try
        {
            var iid = new Guid("C0A651F5-B48B-11D2-B5ED-006097C686F6");
            int hr = Marshal.QueryInterface(unknown, in iid, out var filter);
            if (filter != IntPtr.Zero) Marshal.Release(filter);
            return $"0x{hr:X8}";
        }
        finally { Marshal.Release(unknown); }
    }
    internal void Browse(string path)
    {
        EnsureAccess();
        Marshal.ThrowExceptionForHR(SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, out _));
        try { Marshal.ThrowExceptionForHR(_browser.BrowseToIDList(pidl, 0)); }
        finally { Marshal.FreeCoTaskMem(pidl); }
    }
    internal int? ItemCount()
    {
        EnsureAccess();
        var iid = new Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE");
        if (_browser.GetCurrentView(ref iid, out var view) < 0) return null;
        try
        {
            var count = Marshal.GetDelegateForFunctionPointer<GetCount>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(view), 7 * IntPtr.Size));
            return count(view, 2, out int value) >= 0 ? value : null;
        }
        finally { Marshal.Release(view); }
    }
    private (long Result, int? Count, string TargetClass) RemoveFirstObjectFromOwnView()
    {
        EnsureAccess();
        var iid = new Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE");
        Marshal.ThrowExceptionForHR(_browser.GetCurrentView(ref iid, out var folder));
        IntPtr pidl = IntPtr.Zero, view = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(Method<GetChild>(folder, 6)(folder, 0, out pidl));
            iid = new Guid("000214E3-0000-0000-C000-000000000046");
            Marshal.ThrowExceptionForHR(_browser.GetCurrentView(ref iid, out view));
            Marshal.ThrowExceptionForHR(Method<GetWindow>(view, 3)(view, out var window));
            long result = 0;
            string target = "";
            for (int depth = 0; depth < 12 && window != IntPtr.Zero; depth++, window = GetParent(window))
            {
                GetWindowThreadProcessId(window, out uint pid);
                if (pid != Environment.ProcessId) break; // Never message someone else's view.
                var name = new StringBuilder(128); GetClassName(window, name, name.Capacity); target = name.ToString();
                result = SHShellFolderView_Message(window, 6 /* SFVM_REMOVEOBJECT */, pidl).ToInt64();
                if (ItemCount() == 2) return (result, ItemCount(), target);
            }
            return (result, ItemCount(), target);
        }
        finally
        {
            if (view != IntPtr.Zero) Marshal.Release(view);
            if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
            Marshal.Release(folder);
        }
    }
    private (bool Applied, int Calls, int Services) CheckSiteFilterOnOwnView(string root)
    {
        EnsureAccess();
        if (_filterAttached) { Marshal.ThrowExceptionForHR(((FilterSite)_com).SetFilter(null)); _filterAttached = false; }
        var iid = new Guid("000214E3-0000-0000-C000-000000000046");
        Marshal.ThrowExceptionForHR(_browser.GetCurrentView(ref iid, out var view));
        object? site = null, viewObject = null;
        IntPtr oldSite = IntPtr.Zero, rootPidl = IntPtr.Zero;
        try
        {
            viewObject = Marshal.GetObjectForIUnknown(view);
            var withSite = (ObjectWithSite)viewObject;
            iid = new Guid("00000000-0000-0000-C000-000000000046");
            withSite.GetSite(ref iid, out oldSite);
            if (oldSite != IntPtr.Zero) site = Marshal.GetObjectForIUnknown(oldSite);
            Marshal.ThrowExceptionForHR(SHParseDisplayName(root, IntPtr.Zero, out rootPidl, 0, out _));
            _filter.CallbackRoot = rootPidl;
            _filter.SetPaths(new[] { Path.Combine(root, "Alpha.txt") }, false);
            try
            {
                Marshal.ThrowExceptionForHR(withSite.SetSite(_filter));
                Marshal.ThrowExceptionForHR(Method<RefreshView>(view, 8)(view));
                var watch = Stopwatch.StartNew();
                while (watch.Elapsed < TimeSpan.FromMilliseconds(700)) { Forms.Application.DoEvents(); System.Threading.Thread.Sleep(10); }
                return (ItemCount() == 2, _filter.IncludeCalls, _filter.ServiceQueries);
            }
            finally { Marshal.ThrowExceptionForHR(withSite.SetSite(site)); _filter.CallbackRoot = IntPtr.Zero; }
        }
        finally
        {
            if (rootPidl != IntPtr.Zero) Marshal.FreeCoTaskMem(rootPidl);
            if (site != null) Marshal.ReleaseComObject(site);
            if (oldSite != IntPtr.Zero) Marshal.Release(oldSite);
            if (viewObject != null) Marshal.ReleaseComObject(viewObject);
            Marshal.Release(view);
        }
    }
    public void Dispose()
    {
        if (Environment.CurrentManagedThreadId != _thread) throw new InvalidOperationException("Native Shell view disposed on a different thread.");
        if (!_initialized) return;
        _initialized = false;
        try { if (_filterAttached) ((FilterSite)_com).SetFilter(null); _browser.Destroy(); }
        finally { Marshal.ReleaseComObject(_com); }
    }
    private void EnsureAccess()
    {
        if (!_initialized) throw new ObjectDisposedException(nameof(NativeShellView));
        if (Environment.CurrentManagedThreadId != _thread) throw new InvalidOperationException("Native Shell view accessed on a different thread.");
    }
    internal IntPtr RetainFixtureFolder()
    {
        EnsureAccess();
        var iid = new Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE");
        Marshal.ThrowExceptionForHR(_browser.GetCurrentView(ref iid, out var folder));
        return folder;
    }
    internal static IntPtr FindFixtureList(IntPtr parent)
    {
        IntPtr found = IntPtr.Zero;
        EnumChildren callback = (window, _) =>
        {
            var name = new StringBuilder(128); GetClassName(window, name, name.Capacity);
            if (name.ToString() == "SysListView32") found = window;
            return true;
        };
        EnumChildWindows(parent, callback, IntPtr.Zero); GC.KeepAlive(callback); return found;
    }
    internal int FixtureSelectAll()
    {
        EnsureAccess();
        IntPtr folder = RetainFixtureFolder();
        try
        {
            int count = ItemCount() ?? 0;
            for (int i = 0; i < count; i++) Method<SelectItem>(folder, 15)(folder, i, 1);
            return count;
        }
        finally { Marshal.Release(folder); }
    }
    internal int FixtureSelectedCount()
    {
        EnsureAccess();
        IntPtr folder = RetainFixtureFolder();
        try { Marshal.ThrowExceptionForHR(Method<GetCount>(folder, 7)(folder, 1, out int count)); return count; }
        finally { Marshal.Release(folder); }
    }
    internal static void CheckDesktopProjection(string output)
    {
        Directory.CreateDirectory(output);
        void Stage(string text) => File.AppendAllText(Path.Combine(output, "stages.txt"), text + Environment.NewLine);
        Stage("Start owned shell fixture");
        string fixtures = Path.Combine(output, "shell-projection-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtures);
        foreach (string name in new[] { "Alpha.txt", "Beta.txt", "Gamma.txt" }) File.WriteAllText(Path.Combine(fixtures, name), "Artificial " + name);
        using var parent = new Forms.Form { ShowInTaskbar = false, Width = 640, Height = 480 };
        int ole = OleInitialize(IntPtr.Zero);
        try
        {
            using var view = new NativeShellView(parent.Handle, new Rect { Right = 640, Bottom = 480 }, true);
            Stage("Own legacy Shell view initialized");
            view.Browse(fixtures);
            Stage("Own fixture navigation requested");
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < 8000 && view.ItemCount() != 3) { Forms.Application.DoEvents(); System.Threading.Thread.Sleep(10); }
            var classes = new List<string>(); IntPtr list = IntPtr.Zero;
            EnumChildren callback = (window, _) =>
            {
                var name = new StringBuilder(128); GetClassName(window, name, name.Capacity); classes.Add(name.ToString());
                if (name.ToString() == "SysListView32") list = window;
                return true;
            };
            EnumChildWindows(parent.Handle, callback, IntPtr.Zero);
            GC.KeepAlive(callback);
            Stage("Enumerated own child controls: " + string.Join(",", classes));
            var removedProbe = view.RemoveFirstObjectFromOwnView();
            Stage("Legacy remove result " + removedProbe.Result + "; count " + removedProbe.Count);
            view.SetExcludedPaths(Array.Empty<string>());
            var resetTimer = Stopwatch.StartNew();
            while (resetTimer.ElapsedMilliseconds < 3000 && view.ItemCount() != 3) { Forms.Application.DoEvents(); System.Threading.Thread.Sleep(10); }
            // FilterView is a separate capability experiment. It can alter this
            // view asynchronously and rejects an empty reset on this host.
            // Never contaminate projection acceptance with that unrelated probe.
            bool attached = false, hiddenSelectionRejected = false, bulkSelectionExcluded = false, restored = false;
            bool drawSuppressed = false, hitExcluded = false;
            bool refreshedIdentity = false;
            bool keyboardNavigation = false, accessibilityState = false, accessibilityRestored = false;
            bool hiddenFocusExcluded = false;
            int attachResult = int.MinValue;
            if (list != IntPtr.Zero && view.ItemCount() == 3)
            {
                attachResult = NativeProjectionFixture.KansoAttachOwnedList(list);
                Stage("Attach result " + attachResult);
                attached = attachResult == 0;
                if (attached)
                {
                    try
                    {
                        var iid = new Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE");
                        Marshal.ThrowExceptionForHR(view._browser.GetCurrentView(ref iid, out var folder));
                        try
                        {
                            Marshal.ThrowExceptionForHR(NativeProjectionFixture.KansoSetHiddenShellPaths(folder, new[] { Path.Combine(fixtures, "Beta.txt") }, 1));
                            Stage("Hidden Shell identity set");
                            // Find Beta by native item identity; enumeration order is not assumed.
                            int beta = -1;
                            Marshal.ThrowExceptionForHR(SHParseDisplayName(fixtures, IntPtr.Zero, out var root, 0, out _));
                            try
                            {
                                for (int i = 0; i < 3; i++)
                                {
                                    Marshal.ThrowExceptionForHR(Method<GetChild>(folder, 6)(folder, i, out var child));
                                    IntPtr full = ILCombine(root, child); Marshal.FreeCoTaskMem(child);
                                    try
                                    {
                                        if (SHGetNameFromIDList(full, 0x80058000, out var name) >= 0)
                                        {
                                            try { if (string.Equals(Marshal.PtrToStringUni(name), Path.Combine(fixtures, "Beta.txt"), StringComparison.OrdinalIgnoreCase)) beta = i; }
                                            finally { Marshal.FreeCoTaskMem(name); }
                                        }
                                    }
                                    finally { Marshal.FreeCoTaskMem(full); }
                                }
                            }
                            finally { Marshal.FreeCoTaskMem(root); }
                            if (beta < 0) throw new InvalidOperationException("Fixture Shell identity missing");
                            Marshal.ThrowExceptionForHR(NativeProjectionFixture.KansoFixtureAccessibleState(list, beta, out int annotation));
                            accessibilityState = (annotation & 0x8001) == 0x8001;
                            Stage("Beta index " + beta + "; matcher " + NativeProjectionFixture.KansoIsIndexHidden(beta));
                            hitExcluded = NativeProjectionFixture.HitNativeItem(list, beta) == -1;
                            var drawStats = NativeProjectionFixture.RenderOwnedList(list);
                            var drawDeadline = Stopwatch.StartNew();
                            while (drawStats[0] == 0 && drawDeadline.ElapsedMilliseconds < 2000)
                            {
                                Forms.Application.DoEvents(); System.Threading.Thread.Sleep(10);
                                drawStats = NativeProjectionFixture.RenderOwnedList(list);
                            }
                            drawSuppressed = drawStats[0] > 0;
                            Method<SelectItem>(folder, 15)(folder, beta, 0x15 /* select, deselect others, focus */);
                            NativeProjectionFixture.CheckCommandBoundary(list);
                            Method<GetCount>(folder, 7)(folder, 1, out int selected);
                            Method<GetFocused>(folder, 10)(folder, out int focusedAfterBoundary);
                            hiddenFocusExcluded = focusedAfterBoundary != beta;
                            Stage("Hidden selection: Shell " + selected + "; native " + NativeProjectionFixture.SelectedCount(list));
                            hiddenSelectionRejected = selected == 0;
                            for (int i = 0; i < 3; i++) Method<SelectItem>(folder, 15)(folder, i, 1);
                            NativeProjectionFixture.CheckCommandBoundary(list);
                            Method<GetCount>(folder, 7)(folder, 1, out selected);
                            Stage("Bulk selection: Shell " + selected + "; native " + NativeProjectionFixture.SelectedCount(list));
                            var stats = new uint[4]; NativeProjectionFixture.KansoProjectionStats(stats, stats.Length); Stage("Native stats " + string.Join(",", stats));
                            bulkSelectionExcluded = selected == 2;
                            NativeProjectionFixture.Key(list, 0x24); // Home
                            bool home = NativeProjectionFixture.FocusedIndex(list) == 0;
                            NativeProjectionFixture.Key(list, 0x27); // Right skips Beta
                            bool right = NativeProjectionFixture.FocusedIndex(list) == 2;
                            NativeProjectionFixture.Key(list, 0x25);
                            bool left = NativeProjectionFixture.FocusedIndex(list) == 0;
                            NativeProjectionFixture.Key(list, 0x24); NativeProjectionFixture.Character(list, 'B');
                            bool hiddenType = NativeProjectionFixture.FocusedIndex(list) == 0;
                            NativeProjectionFixture.Key(list, 0x24); NativeProjectionFixture.Character(list, 'G');
                            keyboardNavigation = home && right && left && hiddenType && NativeProjectionFixture.FocusedIndex(list) == 2;
                            File.WriteAllText(Path.Combine(fixtures, "Aardvark.txt"), "Artificial additional original");
                            view.SetExcludedPaths(Array.Empty<string>());
                            var refresh = Stopwatch.StartNew();
                            while (refresh.ElapsedMilliseconds < 3000 && view.ItemCount() != 4) { Forms.Application.DoEvents(); System.Threading.Thread.Sleep(10); }
                            int hiddenCount = 0;
                            for (int i = 0; i < 4; i++)
                            {
                                int hidden = NativeProjectionFixture.KansoIsIndexHidden(i); hiddenCount += hidden;
                                Marshal.ThrowExceptionForHR(NativeProjectionFixture.KansoFixtureAccessibleState(list, i, out int currentAnnotation));
                                if (hidden == 1) accessibilityState &= (currentAnnotation & 0x8001) == 0x8001;
                                else accessibilityState &= (currentAnnotation & 1) == 0;
                            }
                            for (int i = 0; i < 4; i++) Method<SelectItem>(folder, 15)(folder, i, 1);
                            NativeProjectionFixture.CheckCommandBoundary(list);
                            Method<GetCount>(folder, 7)(folder, 1, out selected);
                            refreshedIdentity = view.ItemCount() == 4 && hiddenCount == 1 && selected == 3;
                            NativeProjectionFixture.KansoDetachOwnedList();
                            accessibilityRestored = true;
                            for (int i = 0; i < 4; i++)
                            {
                                Marshal.ThrowExceptionForHR(NativeProjectionFixture.KansoFixtureAccessibleState(list, i, out annotation));
                                accessibilityRestored &= (annotation & 1) == 0;
                            }
                            Method<SelectItem>(folder, 15)(folder, beta, 0x15);
                            Method<GetCount>(folder, 7)(folder, 1, out selected);
                            restored = selected == 1;
                        }
                        finally { Marshal.Release(folder); }
                    }
                    finally { NativeProjectionFixture.KansoDetachOwnedList(); }
                }
            }
            bool originals = true;
            foreach (string name in new[] { "Alpha.txt", "Beta.txt", "Gamma.txt" }) originals &= File.ReadAllText(Path.Combine(fixtures, name)) == "Artificial " + name;
            File.WriteAllText(Path.Combine(output, "shell-projection-result.json"), System.Text.Json.JsonSerializer.Serialize(new { Classes = classes, NativeItemCount = view.ItemCount(), AttachResult = attachResult, Attached = attached, DrawSuppressed = drawSuppressed, HitExcluded = hitExcluded, HiddenSelectionExcludedAtCommandBoundary = hiddenSelectionRejected, HiddenFocusExcludedAtCommandBoundary = hiddenFocusExcluded, BulkSelectionExcludedAtCommandBoundary = bulkSelectionExcluded, IdentityAfterRefreshAndInsertion = refreshedIdentity, KeyboardMessageNavigation = keyboardNavigation, AccessibilityStateAnnotated = accessibilityState, AccessibilityStateRestored = accessibilityRestored, FullAccessibilityInteractionTested = false, Restored = restored, OriginalsUnchanged = originals, ParentShown = parent.Visible, ExplorerAttached = false, RealMouseTested = false, DirectShellSelectionCanTransientlySelectHidden = true }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            if (!attached || !drawSuppressed || !hitExcluded || !hiddenSelectionRejected || !hiddenFocusExcluded || !bulkSelectionExcluded || !refreshedIdentity || !keyboardNavigation || !accessibilityState || !accessibilityRestored || !restored || !originals || parent.Visible) throw new InvalidOperationException("Native Shell projection contract failed; inspect result.json");
            Stage("Checks passed; disposing owned view");
            view.Dispose();
            Stage("Owned view disposed");
        }
        finally { if (ole >= 0) OleUninitialize(); }
        Stage("OLE uninitialized");
        parent.Dispose();
        Stage("Owned parent disposed; returning");
    }
    internal static void Check(string output)
    {
        Directory.CreateDirectory(output);
        string fixtures = Path.Combine(output, "fixture-shell"); Directory.CreateDirectory(fixtures);
        File.WriteAllText(Path.Combine(fixtures, "Alpha.txt"), "Artificial original A");
        File.WriteAllText(Path.Combine(fixtures, "Beta.txt"), "Artificial original B");
        Directory.CreateDirectory(Path.Combine(fixtures, "Folder"));
        // Creating an HWND does not show the form; no Show or desktop attachment.
        using var parent = new Forms.Form { ShowInTaskbar = false, Width = 640, Height = 480 };
        int ole = OleInitialize(IntPtr.Zero);
        try
        {
            using var view = new NativeShellView(parent.Handle, new Rect { Right = 640, Bottom = 480 });
            string filter = view.FilterCapability();
            view.Browse(fixtures);
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(8) && view.ItemCount() != 3) { Forms.Application.DoEvents(); System.Threading.Thread.Sleep(10); }
            int? count = view.ItemCount();
            if (count != 3) throw new InvalidOperationException($"Native Shell view enumeration failed: {count}");
            int cycles = 0;
            for (; cycles < 4; cycles++)
            {
                view.SetExcludedPaths(new[] { Path.Combine(fixtures, "Alpha.txt") });
                WaitCount(2);
                view.SetExcludedPaths(Array.Empty<string>());
                WaitCount(3);
            }
            // A real Shell view can show only group references, while another view
            // shows the complement, without changing the folder's contents.
            view.SetIncludedPaths(new[] { Path.Combine(fixtures, "Alpha.txt").ToUpperInvariant() });
            WaitCount(1);
            view.SetIncludedPaths(Array.Empty<string>());
            WaitCount(0);
            view.SetExcludedPaths(Array.Empty<string>());
            WaitCount(3);
            var siteFilter = view.CheckSiteFilterOnOwnView(fixtures);
            var automationFilter = view.CheckAutomationFilter();
            File.WriteAllText(Path.Combine(output, "automation-filter.json"), System.Text.Json.JsonSerializer.Serialize(automationFilter, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            view.SetExcludedPaths(Array.Empty<string>());
            WaitCount(3);
            var verbs = view.SelectedCanonicalVerbs();
            if (!verbs.Contains("delete") || !verbs.Contains("rename") || !verbs.Contains("properties")) throw new InvalidOperationException("Native context menu is missing standard verbs: " + string.Join(",", verbs));
            var removed = view.RemoveFirstObjectFromOwnView();
            int? countAfterRemove = view.ItemCount();
            view.SetExcludedPaths(Array.Empty<string>());
            WaitCount(3);
            if (parent.Visible || Directory.GetFileSystemEntries(fixtures).Length != 3 || File.ReadAllText(Path.Combine(fixtures, "Alpha.txt")) != "Artificial original A" || File.ReadAllText(Path.Combine(fixtures, "Beta.txt")) != "Artificial original B") throw new InvalidOperationException("Fixture or desktop isolation failed.");
            File.WriteAllText(Path.Combine(output, "shell-view-result.json"), System.Text.Json.JsonSerializer.Serialize(new { NativeItemCount = count, FilterInterface = filter, FilterCycles = cycles, IncludeOnlyAndEmptyGroup = true, NativeMenuVerbs = verbs, ShellRemoveObjectReturn = removed.Result, ShellRemoveTargetClass = removed.TargetClass, CountAfterShellRemove = countAfterRemove, CountAfterShellRefresh = view.ItemCount(), SiteFilterApplied = siteFilter.Applied, SiteIncludeCalls = siteFilter.Calls, SiteServiceQueries = siteFilter.Services, SiteRequests = view._filter.SiteRequests, ParentShown = parent.Visible, OriginalsUnchanged = true, DesktopModified = false, RealInputTested = false }));
            void WaitCount(int expected)
            {
                var timeout = Stopwatch.StartNew();
                while (timeout.Elapsed < TimeSpan.FromSeconds(4) && view.ItemCount() != expected) { Forms.Application.DoEvents(); System.Threading.Thread.Sleep(10); }
                if (view.ItemCount() != expected) throw new InvalidOperationException($"Native filter count {view.ItemCount()}, expected {expected}");
            }
        }
        finally { if (ole >= 0) OleUninitialize(); }
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHParseDisplayName(string name, IntPtr bind, out IntPtr pidl, uint mask, out uint attrs);
    [DllImport("ole32.dll")] private static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")] private static extern void OleUninitialize();
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("shell32.dll")] private static extern IntPtr SHShellFolderView_Message(IntPtr window, uint message, IntPtr value);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int size);
    private delegate bool EnumChildren(IntPtr window, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumChildren callback, IntPtr data);
    [DllImport("shell32.dll")] private static extern IntPtr ILCombine(IntPtr parent, IntPtr child);
    [DllImport("shell32.dll")] private static extern int SHGetNameFromIDList(IntPtr pidl, uint kind, out IntPtr name);
}

[ComVisible(true), Guid("9CC22886-DC8E-11D2-B1D0-00C04F8EEB3E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface INativeShellFilter
{
    [PreserveSig] int ShouldShow(IntPtr folder, IntPtr folderPidl, IntPtr childPidl);
    [PreserveSig] int GetEnumFlags(IntPtr folder, IntPtr folderPidl, out IntPtr window, ref uint flags);
}

[ComVisible(true), Guid("000214F1-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface INativeCommDlgBrowser
{
    [PreserveSig] int OnDefaultCommand(IntPtr view);
    [PreserveSig] int OnStateChange(IntPtr view, uint change);
    [PreserveSig] int IncludeObject(IntPtr view, IntPtr child);
}

[ComVisible(true), Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface INativeSiteServices
{
    [PreserveSig] int QueryService(ref Guid service, ref Guid iid, out IntPtr result);
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class NativeShellFilter : INativeShellFilter, INativeCommDlgBrowser, INativeSiteServices
{
    internal IntPtr CallbackRoot { get; set; }
    internal int IncludeCalls { get; private set; }
    internal int ServiceQueries { get; private set; }
    internal HashSet<string> SiteRequests { get; } = new();
    public int OnDefaultCommand(IntPtr view) => 1;
    public int OnStateChange(IntPtr view, uint change) => 0;
    public int IncludeObject(IntPtr view, IntPtr child) { IncludeCalls++; return ShouldShow(IntPtr.Zero, CallbackRoot, child); }
    public int QueryService(ref Guid service, ref Guid iid, out IntPtr result)
    {
        ServiceQueries++; SiteRequests.Add(service + " / " + iid); result = IntPtr.Zero;
        if (iid != typeof(INativeCommDlgBrowser).GUID) return unchecked((int)0x80004002);
        result = Marshal.GetComInterfaceForObject(this, typeof(INativeCommDlgBrowser)); return 0;
    }
    private sealed record Rule(HashSet<string> Paths, bool IncludeOnly);
    private Rule _rule = new(new(StringComparer.OrdinalIgnoreCase), false);
    internal void SetPaths(IEnumerable<string> paths, bool includeOnly) => System.Threading.Volatile.Write(ref _rule, new Rule(new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase), includeOnly));
    public int ShouldShow(IntPtr folder, IntPtr folderPidl, IntPtr childPidl)
    {
        IntPtr combined = IntPtr.Zero, name = IntPtr.Zero;
        try
        {
            var rule = System.Threading.Volatile.Read(ref _rule);
            combined = ILCombine(folderPidl, childPidl);
            if (combined == IntPtr.Zero || SHGetNameFromIDList(combined, 0x80058000, out name) < 0) return rule.IncludeOnly ? 1 : 0;
            string? path = Marshal.PtrToStringUni(name);
            bool match = path != null && rule.Paths.Contains(path);
            return match == rule.IncludeOnly ? 0 : 1;
        }
        catch { return 0; } // Enumeration must not break for a non-filesystem item.
        finally { if (name != IntPtr.Zero) Marshal.FreeCoTaskMem(name); if (combined != IntPtr.Zero) Marshal.FreeCoTaskMem(combined); }
    }
    public int GetEnumFlags(IntPtr folder, IntPtr folderPidl, out IntPtr window, ref uint flags) { window = IntPtr.Zero; return 0; }
    [DllImport("shell32.dll")] private static extern IntPtr ILCombine(IntPtr parent, IntPtr child);
    [DllImport("shell32.dll")] private static extern int SHGetNameFromIDList(IntPtr pidl, uint kind, out IntPtr name);
}
