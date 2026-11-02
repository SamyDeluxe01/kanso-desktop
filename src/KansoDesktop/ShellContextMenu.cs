using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;

namespace KansoDesktop;

// Owns every COM reference/PIDL/HMENU for one native menu session. No synthetic
// Kanso actions and no filesystem operation unless the user selects a shell verb.
internal sealed class ShellContextMenu : IDisposable
{
    private IntPtr _pidl, _folder, _context, _context2, _context3, _menu;
    private readonly IntPtr _owner;
    internal ShellContextMenu(string path, IntPtr owner, bool extended = false)
    {
        _owner = owner;
        try
        {
            Marshal.ThrowExceptionForHR(SHParseDisplayName(path, IntPtr.Zero, out _pidl, 0, out _));
            Guid folderId = new("000214E6-0000-0000-C000-000000000046");
            Marshal.ThrowExceptionForHR(SHBindToParent(_pidl, ref folderId, out _folder, out var child));
            Guid contextId = new("000214E4-0000-0000-C000-000000000046");
            Marshal.ThrowExceptionForHR(Method<GetUIObject>(_folder, 10)(_folder, owner, 1, new[] { child }, ref contextId, IntPtr.Zero, out _context));
            Guid context2Id = new("000214F4-0000-0000-C000-000000000046"), context3Id = new("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719");
            Marshal.QueryInterface(_context, in context2Id, out _context2);
            Marshal.QueryInterface(_context, in context3Id, out _context3);
            _menu = CreatePopupMenu();
            if (_menu == IntPtr.Zero) throw new InvalidOperationException(L.T("Windows konnte das Kontextmenü nicht erstellen."));
            // CMF_ITEMMENU; CANRENAME requires an inline Shell-view rename host.
            Marshal.ThrowExceptionForHR(Method<QueryMenu>(_context, 3)(_context, _menu, 0, 1, 0x7fff, 0x80u | (extended ? 0x100u : 0)));
        }
        catch { Dispose(); throw; }
    }
    internal int Count => GetMenuItemCount(_menu);
    internal void Show(int x, int y)
    {
        var source = HwndSource.FromHwnd(_owner);
        if (source == null) throw new InvalidOperationException(L.T("Das Gruppenfenster ist nicht mehr verfügbar."));
        source.AddHook(MenuMessage);
        try
        {
            SetForegroundWindow(_owner);
            uint selected = TrackPopupMenuEx(_menu, 0x102, x, y, _owner, IntPtr.Zero);
            if (selected == 0) return;
            var command = new InvokeInfo { Size = Marshal.SizeOf<InvokeInfo>(), Mask = 0x4000 | 0x20000000, Owner = _owner, Verb = new IntPtr(selected - 1), VerbW = new IntPtr(selected - 1), Show = 1, Point = new NativePoint { X = x, Y = y } };
            Marshal.ThrowExceptionForHR(Method<Invoke>(_context, 4)(_context, ref command));
        }
        finally { source.RemoveHook(MenuMessage); PostMessage(_owner, 0, IntPtr.Zero, IntPtr.Zero); }
    }
    private IntPtr MenuMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != 0x117 && msg != 0x2b && msg != 0x2c && msg != 0x120) return IntPtr.Zero;
        if ((msg == 0x2b || msg == 0x2c) && wParam != IntPtr.Zero) return IntPtr.Zero;
        if (_context3 != IntPtr.Zero)
        {
            int hr = Method<HandleMessage2>(_context3, 7)(_context3, (uint)msg, wParam, lParam, out var result);
            if (hr == 0) { handled = true; return result; }
        }
        if (_context2 != IntPtr.Zero && Method<HandleMessage>(_context2, 6)(_context2, (uint)msg, wParam, lParam) == 0) handled = true;
        return IntPtr.Zero;
    }
    internal static void Open(FrameworkElement tile, string path, bool keyboard)
    {
        var window = Window.GetWindow(tile) ?? throw new InvalidOperationException("Kein Gruppenfenster.");
        var point = keyboard ? tile.PointToScreen(new Point(tile.ActualWidth / 2, tile.ActualHeight / 2)) : new Point(System.Windows.Forms.Cursor.Position.X, System.Windows.Forms.Cursor.Position.Y);
        using var menu = new ShellContextMenu(path, new WindowInteropHelper(window).Handle, (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
        menu.Show((int)point.X, (int)point.Y);
    }
    public void Dispose()
    {
        if (_menu != IntPtr.Zero) { DestroyMenu(_menu); _menu = IntPtr.Zero; }
        foreach (var p in new[] { _context3, _context2, _context, _folder }) if (p != IntPtr.Zero) Marshal.Release(p);
        _context3 = _context2 = _context = _folder = IntPtr.Zero;
        if (_pidl != IntPtr.Zero) { Marshal.FreeCoTaskMem(_pidl); _pidl = IntPtr.Zero; }
    }
    private static T Method<T>(IntPtr instance, int slot) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetUIObject(IntPtr self, IntPtr owner, uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] IntPtr[] children, ref Guid iid, IntPtr reserved, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int QueryMenu(IntPtr self, IntPtr menu, uint index, uint first, uint last, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Invoke(IntPtr self, ref InvokeInfo info);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int HandleMessage(IntPtr self, uint msg, IntPtr w, IntPtr l);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int HandleMessage2(IntPtr self, uint msg, IntPtr w, IntPtr l, out IntPtr result);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct InvokeInfo
    {
        public int Size; public uint Mask; public IntPtr Owner, Verb, Parameters, Directory; public int Show; public uint HotKey; public IntPtr Icon, Title, VerbW, ParametersW, DirectoryW, TitleW; public NativePoint Point;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHParseDisplayName(string name, IntPtr bind, out IntPtr pidl, uint attributes, out uint returned);
    [DllImport("shell32.dll")] private static extern int SHBindToParent(IntPtr pidl, ref Guid iid, out IntPtr parent, out IntPtr child);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern int GetMenuItemCount(IntPtr menu);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr owner, IntPtr parameters);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr owner, uint msg, IntPtr w, IntPtr l);
}


