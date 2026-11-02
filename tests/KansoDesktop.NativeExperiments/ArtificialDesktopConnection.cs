using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Forms = System.Windows.Forms;

namespace KansoDesktop;

internal sealed class ArtificialDesktopConnection : IDisposable
{
    private const uint Magic = 0x4B414E53;
    private IntPtr window;
    private readonly ulong token;
    private readonly bool personal;
    [DllImport("KansoProjection.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int KansoFindExistingNativeDesktop(out IntPtr window);
    [DllImport("KansoProjection.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int KansoConnectArtificialDesktop(IntPtr window, UIntPtr token);
    [DllImport("KansoProjection.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] internal static extern int KansoArtificialPathAllowed(string root, UIntPtr token, string path);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeoutW(IntPtr window, uint message, UIntPtr wp, IntPtr lp, uint flags, uint timeout, out UIntPtr result);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeoutW(IntPtr window, uint message, UIntPtr wp, ref CopyData lp, uint flags, uint timeout, out UIntPtr result);
    [StructLayout(LayoutKind.Sequential)] private struct CopyData { public UIntPtr Tag; public uint Length; public IntPtr Data; }
    [DllImport("KansoProjection.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int KansoConnectDesktopReferences(IntPtr window, UIntPtr token);
    internal ArtificialDesktopConnection(ulong token, bool personal = false) { this.token = token; this.personal = personal; }
    internal void Connect()
    {
        Marshal.ThrowExceptionForHR(KansoFindExistingNativeDesktop(out var target));
        if (target == IntPtr.Zero || !IsWindowVisible(target)) throw new InvalidOperationException(L.T("Die vorhandenen Windows-Symbole müssen sichtbar sein. Bisheriges Kanso bitte regulär beenden."));
        window = target;
        int result = personal ? KansoConnectDesktopReferences(window, (UIntPtr)token) : KansoConnectArtificialDesktop(window, (UIntPtr)token);
        if (result != 0) { Dispose(); throw new InvalidOperationException(L.F("Native Verbindung fehlgeschlagen: {0}", result)); }
    }
    private ulong Command(int command)
    {
        if (window == IntPtr.Zero) return 0;
        return SendMessageTimeoutW(window, RegisterWindowMessageW("KansoDesktop.ArtificialFixtureBridge.v1"),
            (UIntPtr)(((ulong)Environment.ProcessId << 16) | (uint)command), new IntPtr(unchecked((long)token)), 3, 300, out var result) != IntPtr.Zero ? result.ToUInt64() : 0;
    }
    internal bool Ping() => Command(2) == Magic;
    internal bool Update(string[] paths)
    {
        if (window == IntPtr.Zero || paths.Length > (personal ? 4096 : 3)) return false;
        byte[] names = Encoding.Unicode.GetBytes(string.Join('\0', paths) + (paths.Length > 0 ? "\0" : ""));
        if (16 + names.Length > 256 * 1024) return false;
        byte[] packet = new byte[16 + names.Length];
        Buffer.BlockCopy(new uint[] { Magic, 1, (uint)Environment.ProcessId, (uint)paths.Length }, 0, packet, 0, 16); names.CopyTo(packet, 16);
        IntPtr memory = Marshal.AllocHGlobal(packet.Length);
        try
        {
            Marshal.Copy(packet, 0, memory, packet.Length);
            var data = new CopyData { Tag = (UIntPtr)token, Length = (uint)packet.Length, Data = memory };
            if (SendMessageTimeoutW(window, 0x4A, UIntPtr.Zero, ref data, 3, 500, out var reply) == IntPtr.Zero || reply.ToUInt64() != Magic) return false;
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 1200)
            {
                ulong status = Command(7);
                if (status == Magic) return true;
                if (status == 1) return false;
                Thread.Sleep(10);
            }
            // Unknown completion cannot leave an ambiguous assignment active.
            Dispose(); return false;
        }
        finally { Marshal.FreeHGlobal(memory); }
    }
    public void Dispose() { if (window != IntPtr.Zero) { Command(3); window = IntPtr.Zero; } }
}



