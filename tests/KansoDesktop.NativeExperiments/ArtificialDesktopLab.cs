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

// Deliberately isolated from the production layout and controller.
internal sealed class ArtificialAssignments
{
    internal readonly string[] Paths;
    internal Dictionary<string, int> Groups { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string[], bool> project;
    internal ArtificialAssignments(string[] paths, Func<string[], bool> projector) { Paths = paths; project = projector; }
    internal bool Assign(IEnumerable<string> input, int group)
    {
        var paths = input.ToArray();
        if (group < 0 || group > 2 || paths.Length == 0 || paths.Any(p => !Paths.Contains(p, StringComparer.OrdinalIgnoreCase))) return false;
        var next = new Dictionary<string, int>(Groups, StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths) { if (group == 0) next.Remove(path); else next[path] = group; }
        if (!project(next.Keys.ToArray())) return false;
        Groups = next; return true;
    }
    internal void Reset() => Groups.Clear();
}

internal sealed class ArtificialDesktopLab : Forms.Form
{
    private const string PrivateFormat = "Kanso.ArtificialReference.v1";
    private readonly string output;
    private readonly ulong token = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)) & 0x7FFFFFFFFFFFFFFFUL;
    private readonly Forms.Label status = new() { AutoSize = false, Dock = Forms.DockStyle.Fill };
    private readonly Forms.Button start = new() { Text = "Künstlichen Desktop-Test starten", AutoSize = true };
    private readonly Forms.Timer timer = new() { Interval = 250 };
    private readonly List<Forms.Form> groups = new();
    private readonly List<Forms.ListBox> lists = new();
    private ArtificialDesktopConnection? connection;
    private ArtificialAssignments? assignments;
    private Mutex? sessionMutex;
    private bool ownsMutex;
    private DateTime deadline;
    private readonly string[] paths;
    internal ArtificialDesktopLab(string output)
    {
        this.output = output;
        if (token == 0) token = 1;
        paths = new[] { "Alpha", "Beta", "Gamma" }.Select(n => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"Kanso-Test-{token:X16}-{n}.txt")).ToArray();
        Text = "Kanso · Nativer Desktop · künstlicher Bedienversuch";
        Size = new Size(660, 320); StartPosition = Forms.FormStartPosition.CenterScreen;
        var panel = new Forms.FlowLayoutPanel { Dock = Forms.DockStyle.Top, Height = 50, Padding = new Forms.Padding(8) };
        panel.Controls.Add(start);
        var stop = new Forms.Button { Text = "Test beenden / Symbole freigeben", AutoSize = true }; panel.Controls.Add(stop);
        Controls.Add(status); Controls.Add(panel);
        status.Padding = new Forms.Padding(16);
        status.Text = "Separater Entwicklungsversuch, keine produktive Umstellung.\r\n\r\nStart erzeugt drei neue Textdateien auf deinem Windows-Desktop und lädt das native Testmodul in Explorer. Nur diese drei Dateien dürfen zugeordnet werden. Das Modul bleibt nach Testende inaktiv bis Explorer regulär endet.\r\n\r\nZwei Testgruppen, maximal 10 Minuten. Keine automatische Bereinigung der Testdateien. Bisheriges Kanso vorher regulär beenden. Echte Drag-/Menübedienung dieser Version ist noch nicht abgenommen.";
        start.Click += (_, _) => Begin(); stop.Click += (_, _) => Stop("Test beendet. Künstliche Dateien bleiben am ursprünglichen Ort.");
        timer.Tick += (_, _) => { if (DateTime.UtcNow >= deadline || connection?.Ping() != true) Stop("Test beendet: Zeitlimit oder native Verbindung verloren. Windows gibt die Symbole spätestens nach der Rückmeldefrist frei."); };
        FormClosed += (_, _) => { Stop("Beendet"); timer.Dispose(); };
    }
    private void Begin()
    {
        if (connection != null) return;
        try
        {
            // Share the production mutex, preventing both launch directions.
            sessionMutex = new Mutex(false, "Local\\KansoDesktopBeta");
            try { ownsMutex = sessionMutex.WaitOne(0); } catch (AbandonedMutexException) { ownsMutex = true; }
            if (!ownsMutex) throw new InvalidOperationException("Die bisherige Kanso-Sitzung läuft noch. Bitte selbst regulär beenden; sie wird hier nicht beendet oder ersetzt.");
            // Preflight before any Desktop writes or native module loading.
            foreach (var process in Process.GetProcessesByName("KansoDesktop")) { using (process) { if (process.Id != Environment.ProcessId) throw new InvalidOperationException("Eine Kanso-Instanz ist noch aktiv. Bitte erst regulär beenden."); } }
            Directory.CreateDirectory(output);
            foreach (string path in paths)
            {
                using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                byte[] content = Encoding.UTF8.GetBytes("Kanso künstliche Testdatei. Kann nach Testende manuell gelöscht werden.\r\n" + Path.GetFileName(path));
                file.Write(content);
            }
            File.WriteAllText(Path.Combine(output, "artificial-session.json"), JsonSerializer.Serialize(new { Paths = paths, Started = DateTimeOffset.Now, PersonalLayoutUsed = false }, new JsonSerializerOptions { WriteIndented = true }));
            connection = new ArtificialDesktopConnection(token); connection.Connect();
            assignments = new ArtificialAssignments(paths, p => connection?.Update(p) == true);
            for (int i = 1; i <= 2; i++) CreateGroup(i);
            start.Enabled = false; deadline = DateTime.UtcNow.AddMinutes(10); timer.Start();
            status.Text = "Test aktiv: Ziehe ausschließlich die drei Kanso-Test-Dateien vom vorhandenen Windows-Desktop in Gruppe A/B.\r\n\r\nHerausziehen auf freie Desktopfläche gibt die Windows-Symbole an ihrer bisherigen Position frei. Escape oder Ablegen über fremden Fenstern erhält die Zuordnung.\r\n\r\nPersönliche Dateien werden abgewiesen. Nach 10 Minuten endet der Test automatisch. Zum sofortigen Beenden den oberen Knopf verwenden.";
        }
        catch (Exception ex) { Stop(ex.Message); }
    }
    private void CreateGroup(int number)
    {
        var form = new Forms.Form { Text = "Kanso Testgruppe " + (number == 1 ? "A" : "B"), Size = new Size(330, 260), StartPosition = Forms.FormStartPosition.Manual };
        var area = Forms.Screen.PrimaryScreen!.WorkingArea; form.Location = new Point(area.Left + 80 + (number - 1) * 360, area.Top + 100);
        var list = new Forms.ListBox { Dock = Forms.DockStyle.Fill, AllowDrop = true, IntegralHeight = false, BackColor = Color.FromArgb(32, 35, 42), ForeColor = Color.White, Font = new Font("Segoe UI", 10), HorizontalScrollbar = true };
        form.Controls.Add(list); lists.Add(list); groups.Add(form);
        string[] DropPaths(Forms.IDataObject? data) => data?.GetDataPresent(PrivateFormat) == true && data.GetData(PrivateFormat) is string path ? new[] { path } : data?.GetData(Forms.DataFormats.FileDrop) as string[] ?? Array.Empty<string>();
        list.DragEnter += (_, e) => { var incoming = DropPaths(e.Data); e.Effect = incoming.Length > 0 && incoming.All(p => paths.Contains(p, StringComparer.OrdinalIgnoreCase)) && (e.AllowedEffect & Forms.DragDropEffects.Link) != 0 ? Forms.DragDropEffects.Link : Forms.DragDropEffects.None; };
        list.DragDrop += (_, e) => { e.Effect = Change(DropPaths(e.Data), number) ? Forms.DragDropEffects.Link : Forms.DragDropEffects.None; };
        Point down = Point.Empty; string? armed = null;
        list.MouseDown += (_, e) => { int index = list.IndexFromPoint(e.Location); armed = e.Button == Forms.MouseButtons.Left && index >= 0 ? list.Items[index] as string : null; down = e.Location; };
        list.MouseUp += (_, _) => armed = null;
        list.MouseMove += (_, e) =>
        {
            if (armed == null || e.Button != Forms.MouseButtons.Left || new Rectangle(down.X - Forms.SystemInformation.DragSize.Width / 2, down.Y - Forms.SystemInformation.DragSize.Height / 2, Forms.SystemInformation.DragSize.Width, Forms.SystemInformation.DragSize.Height).Contains(e.Location)) return;
            string path = armed; armed = null; bool cancelled = false;
            Forms.QueryContinueDragEventHandler track = (_, args) => { if (args.EscapePressed || args.Action == Forms.DragAction.Cancel) cancelled = true; };
            list.QueryContinueDrag += track;
            try
            {
                var data = new Forms.DataObject(); data.SetData(PrivateFormat, path); // Never expose FileDrop to a foreign target.
                var effect = list.DoDragDrop(data, Forms.DragDropEffects.Link);
                if (effect == Forms.DragDropEffects.None && !cancelled && Forms.Control.MouseButtons == Forms.MouseButtons.None && IsDesktop(Forms.Cursor.Position)) Change(new[] { path }, 0);
            }
            finally { list.QueryContinueDrag -= track; }
        };
        form.FormClosing += (_, e) => { if (connection != null) { e.Cancel = true; Stop("Test über Gruppenfenster beendet. Alle Testzuordnungen freigegeben."); } };
        form.Show(this);
    }
    private bool Change(string[] input, int group)
    {
        if (assignments?.Assign(input, group) != true) { Stop("Zuordnung abgelehnt oder Verbindung verloren. Test sicher beendet; keine Datei verschoben."); return false; }
        for (int i = 0; i < lists.Count; i++) { lists[i].Items.Clear(); lists[i].Items.AddRange(assignments.Groups.Where(p => p.Value == i + 1).Select(p => (object)p.Key).ToArray()); }
        return true;
    }
    private void Stop(string message)
    {
        timer.Stop(); connection?.Dispose(); connection = null; assignments?.Reset();
        foreach (var group in groups.ToArray()) group.Dispose(); groups.Clear(); lists.Clear();
        if (ownsMutex) { sessionMutex?.ReleaseMutex(); ownsMutex = false; } sessionMutex?.Dispose(); sessionMutex = null;
        // One run per process: no accidental reuse/overwrite of fixture filenames.
        start.Enabled = false; status.Text = message + "\r\n\r\nFür einen neuen Versuch dieses Testprogramm neu öffnen. Bestehende Testdateien werden nicht automatisch gelöscht.";
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flag);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr window, StringBuilder text, int size);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    private static bool IsDesktop(Point point)
    {
        IntPtr root = GetAncestor(WindowFromPoint(new NativePoint { X = point.X, Y = point.Y }), 2);
        var name = new StringBuilder(128); GetClassNameW(root, name, name.Capacity);
        if (name.ToString() != "Progman" && name.ToString() != "WorkerW") return false;
        GetWindowThreadProcessId(root, out uint pid);
        try { using var process = Process.GetProcessById((int)pid); return string.Equals(process.MainModule?.FileName, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), StringComparison.OrdinalIgnoreCase); } catch { return false; }
    }
    internal static void Run(string output) { Forms.Application.SetHighDpiMode(Forms.HighDpiMode.PerMonitorV2); Forms.Application.EnableVisualStyles(); using var form = new ArtificialDesktopLab(output); Forms.Application.Run(form); }
    internal static void Check(string output)
    {
        Directory.CreateDirectory(output); int checks = 0;
        void Assert(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); checks++; }
        ulong token = 0x123456780ABCDEF0; string root = Path.GetFullPath(output).TrimEnd('\\');
        string[] paths = new[] { "Alpha", "Beta", "Gamma" }.Select(n => Path.Combine(root, $"Kanso-Test-{token:X16}-{n}.txt")).ToArray();
        foreach (string path in paths) Assert(ArtificialDesktopConnection.KansoArtificialPathAllowed(root, (UIntPtr)token, path) == 1, "Native exact allowlist");
        foreach (string bad in new[] { Path.Combine(root, "personal.txt"), paths[0] + ".lnk", paths[0].Replace("Alpha", "Delta"), paths[0].Replace("12345678", "92345678"), Path.Combine(root, "child", Path.GetFileName(paths[0])), Path.Combine(root, "child", "..", Path.GetFileName(paths[0])) }) Assert(ArtificialDesktopConnection.KansoArtificialPathAllowed(root, (UIntPtr)token, bad) == 0, "Native rejected unrelated/ambiguous path");
        Assert(ArtificialDesktopConnection.KansoArtificialPathAllowed(root, (UIntPtr)token, paths[0].ToLowerInvariant()) == 1, "Windows case identity");
        Assert(ArtificialDesktopConnection.KansoArtificialPathAllowed(root, UIntPtr.Zero, paths[0]) == 0, "Zero token rejected");
        bool accept = true; int calls = 0; string[] projected = Array.Empty<string>();
        var state = new ArtificialAssignments(paths, p => { calls++; if (!accept) return false; projected = p; return true; });
        Assert(state.Assign(new[] { paths[0] }, 1) && projected.SequenceEqual(new[] { paths[0] }), "Assign commits native projection");
        Assert(state.Assign(new[] { paths[0] }, 2) && state.Groups[paths[0]] == 2 && projected.Length == 1, "Group transfer remains exclusive");
        accept = false; Assert(!state.Assign(new[] { paths[0] }, 0) && state.Groups[paths[0]] == 2, "Rejected release preserves model");
        accept = true; Assert(state.Assign(new[] { paths[0] }, 0) && state.Groups.Count == 0 && projected.Length == 0, "Release restores native rule");
        int before = calls; Assert(!state.Assign(new[] { paths[1], "C:\\personal.txt" }, 1) && calls == before && state.Groups.Count == 0, "Mixed drop rejected atomically before native call");
        Assert(state.Assign(paths, 1) && state.Groups.Count == 3, "Batch assignment");
        state.Reset(); Assert(state.Groups.Count == 0, "Shutdown clears test model");
        File.WriteAllText(Path.Combine(output, "artificial-lab-checks.json"), JsonSerializer.Serialize(new { Checks = checks, Passed = true, RealExplorerTouched = false, DesktopFilesCreated = false, RealInputUsed = false, InteractiveAcceptance = false }, new JsonSerializerOptions { WriteIndented = true }));
    }
}

