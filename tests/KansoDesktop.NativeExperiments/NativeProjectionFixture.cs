using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Forms = System.Windows.Forms;

namespace KansoDesktop;

// Native control contract test, not an actual desktop/mouse acceptance test.
internal static class NativeProjectionFixture
{
    [DllImport("KansoProjection.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern int KansoAttachOwnedList(IntPtr window);
    [DllImport("KansoProjection.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern int KansoSetHiddenIndices(int[] indices, int length, int expectedCount);
    [DllImport("KansoProjection.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern int KansoDetachOwnedList();
    [DllImport("KansoProjection.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern int KansoIsIndexHidden(int index);
    [DllImport("KansoProjection.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern int KansoFixtureAccessibleState(IntPtr list, int index, out int state);
    [DllImport("KansoProjection.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] internal static extern int KansoSetHiddenShellPaths(IntPtr folder, [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] paths, int count);
    [DllImport("KansoProjection.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern int KansoProjectionStats([Out] uint[] values, int length);
    internal static int SelectedCount(IntPtr list) => SendMessageW(list, 0x1032, IntPtr.Zero, IntPtr.Zero).ToInt32();
    internal static void CheckCommandBoundary(IntPtr list) => SendMessageW(list, 0x0100, new IntPtr(0x87 /* F24: no file action */), IntPtr.Zero);
    internal static void Key(IntPtr list, int key) => SendMessageW(list, 0x0100, new IntPtr(key), IntPtr.Zero);
    internal static void Character(IntPtr list, char value) => SendMessageW(list, 0x0102, new IntPtr(value), IntPtr.Zero);
    internal static int FocusedIndex(IntPtr list) => SendMessageW(list, 0x100C, new IntPtr(-1), new IntPtr(1)).ToInt32();
    internal static uint[] RenderOwnedList(IntPtr list)
    {
        using var bitmap = new Bitmap(640, 480);
        using var graphics = Graphics.FromImage(bitmap);
        IntPtr dc = graphics.GetHdc();
        try { SendMessageW(list, 0x0318, dc, new IntPtr(4)); }
        finally { graphics.ReleaseHdc(dc); }
        var stats = new uint[4]; KansoProjectionStats(stats, stats.Length); return stats;
    }
    internal static int HitNativeItem(IntPtr list, int index)
    {
        var bounds = new NativeRect { Left = 1 /* LVIR_ICON */ };
        if (SendMessageW(list, 0x100E, new IntPtr(index), ref bounds) == IntPtr.Zero) return -2;
        var hit = new HitTest { Point = new Point((bounds.Left + bounds.Right) / 2, (bounds.Top + bounds.Bottom) / 2) };
        return SendMessageW(list, 0x1012, IntPtr.Zero, ref hit).ToInt32();
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr window, uint message, IntPtr wp, ref NativeRect lp);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr window, uint message, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr window, uint message, IntPtr wp, ref ItemState lp);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr window, uint message, IntPtr wp, ref HitTest lp);
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [StructLayout(LayoutKind.Sequential)] private struct ItemState { public uint Mask; public int Index, SubItem; public uint State, StateMask; }
    [StructLayout(LayoutKind.Sequential)] private struct HitTest { public Point Point; public uint Flags; public int Index, SubItem, Group; }

    internal static void Check(string output)
    {
        Directory.CreateDirectory(output);
        string root = Path.Combine(output, "projection-fixtures");
        Directory.CreateDirectory(root);
        string[] names = { "Alpha.txt", "Beta.txt", "Gamma.txt" };
        foreach (string name in names) File.WriteAllText(Path.Combine(root, name), "Artificial " + name);
        using var parent = new Forms.Form { ShowInTaskbar = false, Width = 640, Height = 480 };
        using var images = new Forms.ImageList { ImageSize = new Size(32, 32), ColorDepth = Forms.ColorDepth.Depth32Bit };
        images.Images.Add(SystemIcons.Application);
        using var list = new Forms.ListView { View = Forms.View.LargeIcon, Width = 640, Height = 480, MultiSelect = true, LargeImageList = images, AutoArrange = false, LabelEdit = true };
        parent.Controls.Add(list);
        foreach (string name in names) list.Items.Add(name, 0);
        _ = parent.Handle; _ = list.Handle;
        for (int i = 0; i < names.Length; i++) list.Items[i].Position = new Point(30 + i * 130, 40);
        Forms.Application.DoEvents();
        int checks = 0;
        Require(KansoFixtureAccessibleState(list.Handle, 1, out int baselineAccessibility) == 0, "Read native accessibility baseline");
        Require(KansoAttachOwnedList(parent.Handle) == -3, "Reject non-list window");
        Require(KansoAttachOwnedList(GetDesktopWindow()) == -2, "Reject foreign process without modification");
        int otherThread = System.Threading.Tasks.Task.Run(() => KansoAttachOwnedList(list.Handle)).GetAwaiter().GetResult();
        Require(otherThread == -2, "Reject foreign thread without modification");
        using (var oversized = new Forms.ListView { VirtualMode = true, VirtualListSize = 4097 })
        {
            oversized.RetrieveVirtualItem += (_, e) => e.Item = new Forms.ListViewItem("Artificial virtual item");
            parent.Controls.Add(oversized);
            Require(KansoAttachOwnedList(oversized.Handle) == -8, "Reject oversized native inventory before callbacks are installed");
        }
        int attached = KansoAttachOwnedList(list.Handle);
        Require(attached == 0, "Attach owned native list: " + attached);
        try
        {
            list.Items[1].Selected = true;
            list.Items[1].Focused = true;
            Require(KansoSetHiddenIndices(new[] { 1 }, 1, 3) == 0, "Hide selected item");
            Require(!list.Items[1].Selected && !list.Items[1].Focused, "Remove hidden selection and focus");
            // WinForms supplies its own accessibility provider. Record whether
            // it honors HWND annotations, rather than confusing it with the
            // stock Windows provider tested separately on the real Shell view.
            bool customProviderAnnotation = KansoFixtureAccessibleState(list.Handle, 1, out int hiddenAccessibility) == 0 && (hiddenAccessibility & 0x8001) == 0x8001;
            Require(KansoFixtureAccessibleState(list.Handle, 0, out int visibleAccessibility) == 0 && (visibleAccessibility & 1) == 0, "Visible entry remains available to accessibility");
            SetState(-1, 2, 2);
            Require(list.Items[0].Selected && !list.Items[1].Selected && list.Items[2].Selected, "Select all includes only visible entries");
            SetState(1, 3, 3);
            Require(!list.Items[1].Selected && !list.Items[1].Focused, "Reject direct hidden selection/focus");
            Require(Hit(1) == -1 && Hit(0) == 0 && Hit(2) == 2, "Hit test excludes only hidden entry");
            Require(SendMessageW(list.Handle, 0x1076 /* EDITLABELW */, new IntPtr(1), IntPtr.Zero) == IntPtr.Zero, "Reject hidden rename");
            Require(KansoSetHiddenIndices(new[] { 9 }, 1, 3) == -3 && Hit(1) == -1, "Invalid update preserves valid rule");
            Require(list.Items.Count == 3 && SendMessageW(list.Handle, 0x1004, IntPtr.Zero, IntPtr.Zero).ToInt32() == 3, "Native inventory unchanged");
            using var bitmap = new Bitmap(640, 480);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                IntPtr dc = graphics.GetHdc();
                try { SendMessageW(list.Handle, 0x0318 /* PRINTCLIENT */, dc, new IntPtr(4)); }
                finally { graphics.ReleaseHdc(dc); }
            }
            var stats = new uint[4]; KansoProjectionStats(stats, stats.Length);
            Require(stats[0] > 0, "Real native custom draw suppresses hidden entry");
            Require(stats[1] > 0 && stats[2] > 0, "Native selection and hit filters exercised");
            Key(list.Handle, 0x24 /* Home */);
            Require(FocusedIndex(list.Handle) == 0, "Home focuses first visible entry");
            Key(list.Handle, 0x27 /* Right */);
            Require(FocusedIndex(list.Handle) == 2 && !list.Items[1].Selected, "Right skips hidden entry");
            Key(list.Handle, 0x25 /* Left */);
            Require(FocusedIndex(list.Handle) == 0, "Left skips hidden entry");
            Key(list.Handle, 0x23 /* End */);
            Require(FocusedIndex(list.Handle) == 2, "End focuses last visible entry");
            Key(list.Handle, 0x24); Character(list.Handle, 'B');
            Require(FocusedIndex(list.Handle) == 0 && !list.Items[1].Selected, "Type search excludes hidden entry");
            Key(list.Handle, 0x24); Character(list.Handle, 'G');
            Require(FocusedIndex(list.Handle) == 2 && list.Items[2].Selected, "Type search reaches visible entry");
            // Any insertion reveals all items; stale indices must never target
            // another original. Bulk selection must keep working after reveal.
            list.Items.Add("Inserted fixture", 0);
            Require(Hit(1) == 1, "Insertion invalidates suppression snapshot");
            SetState(-1, 2, 2);
            Require(list.SelectedItems.Count == 4, "Select all remains functional after invalidation");
            Require(KansoSetHiddenIndices(new[] { 1 }, 1, 3) == -2 && Hit(1) == 1, "Count mismatch fails open");
            Require(KansoSetHiddenIndices(new[] { 1 }, 1, 4) == 0 && Hit(1) == -1, "Reapply with current inventory");
            Require(KansoDetachOwnedList() == 0 && Hit(1) == 1, "Detach restores ordinary hit test");
            Require(KansoFixtureAccessibleState(list.Handle, 1, out int restoredAccessibility) == 0 && (restoredAccessibility & 0x8001) == (baselineAccessibility & 0x8001), "Detach clears accessibility annotation");
            SetState(-1, 2, 2);
            Require(list.SelectedItems.Count == 4, "Detach restores ordinary selection");
            foreach (string name in names) Require(File.ReadAllText(Path.Combine(root, name)) == "Artificial " + name, "Artificial original unchanged");
            Require(Directory.GetFiles(root).Length == names.Length && !parent.Visible, "Isolated files and hidden test parent");
            File.WriteAllText(Path.Combine(output, "projection-result.json"), JsonSerializer.Serialize(new { Checks = checks, NativeDrawSkips = stats[0], SelectionVetoes = stats[1], HitSkips = stats[2], OriginalFilesUnchanged = true, NativeItemsRetained = true, ParentShown = parent.Visible, RealMouseTested = false, ExplorerAttached = false, CustomWinFormsProviderHonorsAnnotation = customProviderAnnotation, FullAccessibilityInteractionTested = false }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { KansoDetachOwnedList(); }

        void SetState(int index, uint state, uint mask)
        {
            var value = new ItemState { State = state, StateMask = mask };
            SendMessageW(list.Handle, 0x102B, new IntPtr(index), ref value);
        }
        int Hit(int index)
        {
            Rectangle bounds = list.Items[index].GetBounds(Forms.ItemBoundsPortion.Icon);
            var hit = new HitTest { Point = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2) };
            return SendMessageW(list.Handle, 0x1012, IntPtr.Zero, ref hit).ToInt32();
        }
        void Require(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks++; }
    }
}
