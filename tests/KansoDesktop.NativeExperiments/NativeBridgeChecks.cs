using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Forms = System.Windows.Forms;

namespace KansoDesktop;

internal static class NativeBridgeChecks
{
    private const uint Magic = 0x4B414E53;
    private const string TokenProperty = "Kanso.NativeFixture.Token.v1", OwnerProperty = "Kanso.NativeFixture.Owner.v1", FolderProperty = "Kanso.NativeFixture.Folder.v1";
    internal sealed record Configuration(string Root, string Ready, string Status, string Phase, string Command, string Quit, long Token, int OwnerPid, string Scenario);
    private sealed record ReadyInfo(long Window, int Pid);
    private sealed record StatusInfo(bool Active, int Hidden, int Selected, int ShellSelected, int AccessibilityUnavailable, uint DrawSkips, long Tick, bool ParentShown);
    [DllImport("KansoProjection.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int KansoConnectArtificialFixture(IntPtr window, uint expectedPid, UIntPtr token);
    [DllImport("KansoProjection.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int KansoArtificialFixtureActive();
    [DllImport("KansoProjection.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int KansoFindExistingNativeDesktop(out IntPtr window);
    [DllImport("KansoProjection.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int KansoObserveExistingNativeDesktop(IntPtr window, UIntPtr token);
    [DllImport("KansoProjection.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int KansoObservationDiagnostic([Out] int[] values, int length);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetPropW(IntPtr window, string name, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr RemovePropW(IntPtr window, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SendMessageTimeoutW(IntPtr window, uint message, UIntPtr wp, IntPtr lp, uint flags, uint timeout, out UIntPtr result);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SendMessageTimeoutW(IntPtr window, uint message, UIntPtr wp, ref CopyData lp, uint flags, uint timeout, out UIntPtr result);
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [StructLayout(LayoutKind.Sequential)] private struct CopyData { public UIntPtr Tag; public uint Length; public IntPtr Data; }

    internal static void RunFixture(string configuration)
    {
        var c = Read<Configuration>(configuration);
        using var parent = new Forms.Form { ShowInTaskbar = false, Width = 640, Height = 480 };
        using var view = new NativeShellView(parent.Handle, new NativeShellView.Rect { Right = 640, Bottom = 480 }, true);
        view.Browse(c.Root);
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < 8000 && view.ItemCount() != (c.Scenario == "personal" ? 6 : 3)) { Forms.Application.DoEvents(); System.Threading.Thread.Sleep(10); }
        if (view.ItemCount() != (c.Scenario == "personal" ? 6 : 3)) throw new InvalidOperationException("Remote fixture enumeration failed");
        IntPtr list = NativeShellView.FindFixtureList(parent.Handle), folder = view.RetainFixtureFolder();
        if (list == IntPtr.Zero) { Marshal.Release(folder); throw new InvalidOperationException("Remote native list absent"); }
        try
        {
            if (!SetPropW(list, TokenProperty, new IntPtr(c.Token)) || !SetPropW(list, OwnerProperty, new IntPtr(c.OwnerPid)) || !SetPropW(list, FolderProperty, folder)) throw new InvalidOperationException("Fixture publication failed");
            if (c.Scenario == "personal") SetPropW(list, "Kanso.NativeFixture.References.v1", new IntPtr(1));
            if (c.Scenario == "artificial") SetPropW(list, "Kanso.NativeFixture.Artificial.v1", new IntPtr(1));
            Write(c.Ready, new ReadyInfo(list.ToInt64(), Environment.ProcessId));
            long previous = -1000;
            while (timer.ElapsedMilliseconds < 30000 && !File.Exists(c.Quit))
            {
                Forms.Application.DoEvents();
                if (timer.ElapsedMilliseconds - previous >= 80)
                {
                    previous = timer.ElapsedMilliseconds;
                    view.FixtureSelectAll();
                    NativeProjectionFixture.CheckCommandBoundary(list);
                    var stats = NativeProjectionFixture.RenderOwnedList(list);
                    int hidden = 0;
                    int unavailable = 0;
                    for (int i = 0; i < (c.Scenario == "personal" ? 6 : 3); i++)
                    {
                        hidden += NativeProjectionFixture.KansoIsIndexHidden(i);
                        Marshal.ThrowExceptionForHR(NativeProjectionFixture.KansoFixtureAccessibleState(list, i, out int accessible));
                        if ((accessible & 1) != 0) unavailable++;
                    }
                    Write(c.Status, new StatusInfo(KansoArtificialFixtureActive() != 0, hidden, NativeProjectionFixture.SelectedCount(list), view.FixtureSelectedCount(), unavailable, stats[0], previous, parent.Visible));
                }
                System.Threading.Thread.Sleep(5);
            }
        }
        finally
        {
            NativeProjectionFixture.KansoDetachOwnedList();
            RemovePropW(list, "Kanso.NativeFixture.Artificial.v1");
            RemovePropW(list, "Kanso.NativeFixture.References.v1");
            RemovePropW(list, FolderProperty); RemovePropW(list, OwnerProperty); RemovePropW(list, TokenProperty);
            Marshal.Release(folder);
        }
    }

    internal static void RunOwner(string configuration)
    {
        var c = Read<Configuration>(configuration);
        Wait(() => File.Exists(c.Ready), 10000, "Fixture ready");
        var ready = Read<ReadyInfo>(c.Ready); IntPtr window = new(ready.Window);
        int foreign = KansoConnectArtificialFixture(GetDesktopWindow(), (uint)ready.Pid, (UIntPtr)(ulong)c.Token);
        int wrongToken = KansoConnectArtificialFixture(window, (uint)ready.Pid, (UIntPtr)(ulong)(c.Token + 1));
        if (foreign != -1 || wrongToken != -1) throw new InvalidOperationException("Bridge target guard failed");
        int connected = KansoConnectArtificialFixture(window, (uint)ready.Pid, (UIntPtr)(ulong)c.Token);
        if (connected != 0) throw new InvalidOperationException("Fixture bridge attach: " + connected);
        if (!Update(window, c, new[] { FixturePath(c, "Beta.txt") })) throw new InvalidOperationException("Fixture rule IPC rejected");
        if (Update(window, c with { Token = c.Token + 1 }, new[] { FixturePath(c, "Alpha.txt") })) throw new InvalidOperationException("Wrong packet token accepted");
        if (Update(window, c, new[] { FixturePath(c, "Beta.txt"), Path.Combine(c.Root, "..", "outside-artificial.txt") })) throw new InvalidOperationException("Outside fixture path accepted");
        byte[] malformed = new byte[16]; Buffer.BlockCopy(new uint[] { Magic, 1, (uint)Environment.ProcessId, 4097 }, 0, malformed, 0, 16);
        if (SendPacket(window, c, malformed)) throw new InvalidOperationException("Oversized rule count accepted");
        byte[] unterminated = new byte[18]; Buffer.BlockCopy(new uint[] { Magic, 1, (uint)Environment.ProcessId, 1 }, 0, unterminated, 0, 16); unterminated[16] = (byte)'x';
        if (SendPacket(window, c, unterminated)) throw new InvalidOperationException("Unterminated path accepted");
        if (c.Scenario == "personal" && !Update(window, c, new[] { FixturePath(c,"Alpha.txt"), FixturePath(c,"Beta.txt"), Path.Combine(c.Root,"Delta.txt"), Path.Combine(c.Root,"Ordinary folder") })) throw new InvalidOperationException("Ordinary files and folder batch rejected");
        Write(c.Phase, new { Attached = true, ForeignRejected = true, WrongTokenRejected = true, InvalidPacketsRejected = 4 });
        var timer = Stopwatch.StartNew();
        long ping = -1000;
        while (timer.ElapsedMilliseconds < 15000)
        {
            if (File.Exists(c.Command) && File.ReadAllText(c.Command) == "detach")
            {
                if (!Command(window, c, 3)) throw new InvalidOperationException("Fixture detach failed");
                return;
            }
            if (c.Scenario == "timeout")
            {
                if (timer.ElapsedMilliseconds > 4000) return;
            }
            else if (timer.ElapsedMilliseconds - ping > 250)
            {
                if (!Command(window, c, 2)) throw new InvalidOperationException("Fixture heartbeat rejected");
                ping = timer.ElapsedMilliseconds;
            }
            System.Threading.Thread.Sleep(20);
        }
        throw new TimeoutException("Owner test deadline");
    }

    internal static void Check(string output)
    {
        Directory.CreateDirectory(output);
        var results = new List<object>();
        foreach (string scenario in new[] { "detach", "timeout", "crash", "artificial", "personal" })
        {
            string run = Path.Combine(output, scenario + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(run);
            string root = Path.Combine(run, "artificial-originals"); Directory.CreateDirectory(root);
            
            long token = BitConverter.ToInt64(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)) & long.MaxValue;
            if (token == 0) token = 1;
            string ownerConfig = Path.Combine(run, "owner.json"), fixtureConfig = Path.Combine(run, "fixture.json");
            var c = new Configuration(root, Path.Combine(run, "ready.json"), Path.Combine(run, "status.json"), Path.Combine(run, "phase.json"), Path.Combine(run, "command.txt"), Path.Combine(run, "quit.txt"), token, 0, scenario);
            foreach (string name in new[] { "Alpha.txt", "Beta.txt", "Gamma.txt" }) File.WriteAllText(FixturePath(c, name), "Artificial " + name);
            if (scenario == "personal") { File.WriteAllText(Path.Combine(root, "Delta.txt"), "extra"); File.WriteAllText(Path.Combine(root, "Epsilon.txt"), "extra"); Directory.CreateDirectory(Path.Combine(root, "Ordinary folder")); }
            Write(ownerConfig, c);
            using var owner = Launch("bridge-owner", ownerConfig);
            c = c with { OwnerPid = owner.Id }; Write(fixtureConfig, c);
            using var fixture = Launch("bridge-fixture", fixtureConfig);
            try
            {
                Wait(() => File.Exists(c.Phase) || owner.HasExited, 11000, "Owner attach handshake");
                if (!File.Exists(c.Phase)) throw new InvalidOperationException("Owner exited before handshake: " + owner.ExitCode);
                Wait(() => TryStatus(c.Status, s => s.Active && s.Hidden == (scenario == "personal" ? 4 : 1) && s.Selected == 2 && s.ShellSelected == 2 && s.AccessibilityUnavailable == (scenario == "personal" ? 4 : 1) && s.DrawSkips > 0 && !s.ParentShown), 2000, "Remote artificial projection");
                var recovery = Stopwatch.StartNew();
                if (scenario == "crash") { owner.Kill(); owner.WaitForExit(2000); }
                else if ((scenario == "detach" || scenario == "artificial" || scenario == "personal")) File.WriteAllText(c.Command, "detach");
                Wait(() => TryStatus(c.Status, s => !s.Active && s.Hidden == 0 && s.Selected == (scenario == "personal" ? 6 : 3) && s.ShellSelected == (scenario == "personal" ? 6 : 3) && s.AccessibilityUnavailable == 0), 4500, "Native recovery");
                long recoveredMs = recovery.ElapsedMilliseconds;
                if (fixture.HasExited) throw new InvalidOperationException("Fixture host died during recovery");
                foreach (string name in new[] { "Alpha.txt", "Beta.txt", "Gamma.txt" })
                    if (File.ReadAllText(FixturePath(c, name)) != "Artificial " + name) throw new InvalidOperationException("Artificial original altered");
                if (Directory.GetFileSystemEntries(root).Length != (scenario == "personal" ? 6 : 3)) throw new InvalidOperationException("Artificial inventory altered");
                results.Add(new { Scenario = scenario, RecoveredMilliseconds = recoveredMs, FixtureSurvived = true, OriginalsUnchanged = true, RealExplorerTouched = false, RealInputUsed = false });
            }
            finally
            {
                File.WriteAllText(c.Quit, "quit");
                if (!fixture.WaitForExit(2000)) fixture.Kill();
                if (!owner.HasExited) owner.Kill();
            }
        }
        Write(Path.Combine(output, "bridge-result.json"), new { Scenarios = results, ProductionIntegrated = false, RealExplorerUsed = false, NativeDesktopObservationImplemented = true, NativeDesktopObservationExecuted = false, MsaaStateRecoveryVerified = true, FullAccessibilityInteractionTested = false });
    }

    internal static void ObserveDesktop(string output)
    {
        Directory.CreateDirectory(output);
        Marshal.ThrowExceptionForHR(KansoFindExistingNativeDesktop(out IntPtr window));
        if (window == IntPtr.Zero) throw new InvalidOperationException("Existing desktop unavailable");
        bool visibleBefore = IsWindowVisible(window);
        GetWindowThreadProcessId(window, out uint explorerPid);
        long token = BitConverter.ToInt64(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)) & long.MaxValue;
        if (token == 0) token = 1;
        var c = new Configuration("", "", "", "", "", "", token, Environment.ProcessId, "observe");
        int connected = KansoObserveExistingNativeDesktop(window, (UIntPtr)(ulong)token);
        var diagnostic = new int[8]; KansoObservationDiagnostic(diagnostic, diagnostic.Length);
        Write(Path.Combine(output, "observation-connection.json"), new { Connected = connected, HookEntered = diagnostic[0], DesktopIdentity = diagnostic[1], FolderHResult = $"0x{diagnostic[2]:X8}", Preconditions = diagnostic[3], Attached = diagnostic[4], SendSucceeded = diagnostic[5], Reply = diagnostic[6], LastWin32Error = diagnostic[7] });
        if (connected != 0) throw new InvalidOperationException("Native observation attach: " + connected);
        bool nonemptyRejected = false, detached = false;
        try
        {
            // Rejected at the packet header, before path processing. It cannot
            // suppress any original or read the deliberately nonexistent path.
            nonemptyRejected = !Update(window, c, new[] { "C:\\KansoObservationMustNeverBeParsed" });
            if (!nonemptyRejected) throw new InvalidOperationException("Observation accepted a nonempty rule");
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 3000)
            {
                if (!Command(window, c, 2)) throw new InvalidOperationException("Native observation heartbeat failed");
                System.Threading.Thread.Sleep(150);
            }
            detached = Command(window, c, 3);
            if (!detached || Command(window, c, 4)) throw new InvalidOperationException("Observation did not detach");
            GetWindowThreadProcessId(window, out uint afterPid);
            if (afterPid != explorerPid || IsWindowVisible(window) != visibleBefore) throw new InvalidOperationException("Native host or visibility changed during observation");
            Write(Path.Combine(output, "desktop-observation-result.json"), new { ExistingNativeDesktopConnected = true, ObservationMilliseconds = watch.ElapsedMilliseconds, NonemptyRulesRejected = nonemptyRejected, Detached = detached, SameExplorerProcess = true, VisibilityUnchanged = true, OriginalItemsExcluded = 0, RealMouseOrKeyboardInput = false, ProductionIntegrated = false, NativeModuleResidentUntilExplorerExit = true });
        }
        finally { if (!detached) Command(window, c, 3); }
    }

    private static string FixturePath(Configuration c, string name) => Path.Combine(c.Root, c.Scenario == "artificial" ? $"Kanso-Test-{c.Token:X16}-{name}" : name);

    private static bool Update(IntPtr window, Configuration c, string[] paths)
    {
        byte[] names = Encoding.Unicode.GetBytes(string.Join('\0', paths) + (paths.Length > 0 ? "\0" : ""));
        byte[] packet = new byte[16 + names.Length];
        Buffer.BlockCopy(new uint[] { Magic, 1, (uint)Environment.ProcessId, (uint)paths.Length }, 0, packet, 0, 16); names.CopyTo(packet, 16);
        if (!SendPacket(window, c, packet)) return false;
        if (c.Scenario != "artificial" && c.Scenario != "personal") return true;
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 1200) {
            if (SendMessageTimeoutW(window, RegisterWindowMessageW("KansoDesktop.ArtificialFixtureBridge.v1"), (UIntPtr)(((ulong)Environment.ProcessId << 16) | 7), new IntPtr(c.Token), 3, 300, out var result) == IntPtr.Zero) return false;
            if (result.ToUInt64() == Magic) return true;
            if (result.ToUInt64() == 1) return false;
            System.Threading.Thread.Sleep(10);
        }
        return false;
    }
    private static bool SendPacket(IntPtr window, Configuration c, byte[] packet)
    {
        IntPtr memory = Marshal.AllocHGlobal(packet.Length);
        try
        {
            Marshal.Copy(packet, 0, memory, packet.Length);
            var data = new CopyData { Tag = (UIntPtr)(ulong)c.Token, Length = (uint)packet.Length, Data = memory };
            return SendMessageTimeoutW(window, 0x004A, UIntPtr.Zero, ref data, 3, 1000, out var result) != IntPtr.Zero && result.ToUInt64() == Magic;
        }
        finally { Marshal.FreeHGlobal(memory); }
    }
    private static bool Command(IntPtr window, Configuration c, int command) => SendMessageTimeoutW(window, RegisterWindowMessageW("KansoDesktop.ArtificialFixtureBridge.v1"), (UIntPtr)(((ulong)Environment.ProcessId << 16) | (uint)command), new IntPtr(c.Token), 3, 1000, out var result) != IntPtr.Zero && result.ToUInt64() == Magic;
    private static bool TryStatus(string file, Func<StatusInfo, bool> predicate) { try { return predicate(Read<StatusInfo>(file)); } catch (IOException) { return false; } catch (JsonException) { return false; } }
    private static T Read<T>(string file)
    {
        // Status snapshots are atomically replaced by another process. Readers
        // must allow replacement while they hold the previous snapshot open.
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<T>(stream)!;
    }
    private static void Write<T>(string file, T value)
    {
        string temporary = file + ".tmp"; File.WriteAllText(temporary, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true })); File.Move(temporary, file, true);
    }
    private static void Wait(Func<bool> condition, int milliseconds, string step)
    {
        var watch = Stopwatch.StartNew();
        while (!condition()) { if (watch.ElapsedMilliseconds > milliseconds) throw new TimeoutException(step); System.Threading.Thread.Sleep(15); }
    }
    private static Process Launch(string mode, string argument)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(NativeBridgeChecks).Assembly.Location);
        start.ArgumentList.Add(mode); start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new IOException("Fixture child launch failed");
    }
}

