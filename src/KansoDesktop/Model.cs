using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace KansoDesktop;

public sealed class FenceModel
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "Neue Gruppe";
    public double X { get; set; } = 120;
    public double Y { get; set; } = 120;
    public double Width { get; set; } = 380;
    public double Height { get; set; } = 330;
    public bool Collapsed { get; set; }
    public List<string> Paths { get; set; } = new();
}

public sealed class Settings
{
    public int Schema { get; set; } = 1;
    public string Language { get; set; } = "de";
    public string Theme { get; set; } = "Japanese";
    public string Accent { get; set; } = "#DFA0B6";
    public string Panel { get; set; } = "#17141D";
    public double PanelOpacity { get; set; } = 0.55;
    public bool Animations { get; set; } = true;
    public bool AutoCollapse { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;
    public bool ResumeDesktop { get; set; } = true;
    public bool DesktopModeRequested { get; set; } = true;
    public int IconSize { get; set; } = 36;
    public List<FenceModel> Fences { get; set; } = new();
    public Dictionary<string, DesktopPoint> DesktopPositions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public FenceModel Inbox { get; set; } = new() { Title = "Nicht zugeordnet", X = 90, Y = 560, Width = 600, Height = 360 };

    public void Normalize()
    {
        if (Schema != 1) throw new InvalidDataException(L.T("Diese Layout-Version wird noch nicht unterstützt."));
        Fences ??= new();
        if ((DesktopPositions?.Count ?? 0) > 20000 || Fences.Count > 256 || Fences.Any(f => f == null) || Fences.Sum(f => (long)(f.Paths?.Count ?? 0)) > 20000) throw new InvalidDataException(L.T("Layout überschreitet die unterstützte Anzahl an Gruppen oder Elementen."));
        Inbox ??= new() { Title = "Nicht zugeordnet" };
        DesktopPositions = (DesktopPositions ?? new()).Where(p => !string.IsNullOrWhiteSpace(p.Key) && p.Value != null).GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);
        Accent = ValidColor(Accent, "#DFA0B6"); Panel = ValidColor(Panel, "#17141D");
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>();
        foreach (var f in Fences)
        {
            if (string.IsNullOrWhiteSpace(f.Id) || !ids.Add(f.Id)) f.Id = Guid.NewGuid().ToString("N");
            f.Width = Clamp(f.Width, 240, 1600, 380);
            f.Height = Clamp(f.Height, 150, 1800, 330);
            f.X = double.IsFinite(f.X) ? f.X : 120;
            f.Y = double.IsFinite(f.Y) ? f.Y : 120;
            f.Title = string.IsNullOrWhiteSpace(f.Title) ? "Gruppe" : f.Title.Trim();
            f.Paths = (f.Paths ?? new()).Where(p => !string.IsNullOrWhiteSpace(p))
                .Where(p => owned.Add(p)).ToList();
        }
        PanelOpacity = Clamp(PanelOpacity, 0.08, 0.95, 0.55);
        IconSize = Math.Clamp(IconSize, 24, 64);
    }
    private static string ValidColor(string? value, string fallback) => value != null && System.Text.RegularExpressions.Regex.IsMatch(value, "^#[0-9a-fA-F]{6}$") ? value : fallback;
    private static double Clamp(double v, double min, double max, double fallback) =>
        double.IsFinite(v) ? Math.Clamp(v, min, max) : fallback;

    public void Assign(string path, FenceModel target)
    {
        path = Path.GetFullPath(path);
        foreach (var f in Fences) f.Paths.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        target.Paths.Add(path);
    }
}

public sealed class LayoutStore
{
    public string FileName { get; }
    public LayoutStore(string file) { FileName = file; }
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public Settings Load()
    {
        if (!File.Exists(FileName)) return new();
        if (new FileInfo(FileName).Length > 8 * 1024 * 1024) throw new InvalidDataException(L.T("Layout-Datei ist zu groß (maximal 8 MB)."));
        // Fail visibly instead of silently overwriting an unreadable layout.
        var result = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FileName))
            ?? throw new InvalidDataException(L.T("Leere Layout-Datei."));
        result.Normalize();
        return result;
    }
    public void Save(Settings settings)
    {
        settings.Normalize();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FileName))!);
        var temp = FileName + ".tmp";
        var json = JsonSerializer.Serialize(settings, Options); if (System.Text.Encoding.UTF8.GetByteCount(json) > 8 * 1024 * 1024) throw new InvalidDataException(L.T("Layout ist zu groß (maximal 8 MB)."));
        File.WriteAllText(temp, json);
        if (File.Exists(FileName)) File.Replace(temp, FileName, FileName + ".bak", true);
        else File.Move(temp, FileName);
    }
    public static void Export(Settings settings, string file) =>
        File.WriteAllText(file, JsonSerializer.Serialize(settings, Options));
}

public record Theme(string Name, string Subtitle, string Accent, string Panel, string Text);
public static class Themes
{
    public static readonly Theme[] All = {
        new("Japanese", "Sakura · ruhige Kontraste", "#DFA0B6", "#17141D", "#F5EEF2"),
        new("Anime", "Lavendel · weiche Akzente", "#BCA7FF", "#1B1930", "#F2EDFF"),
        new("Simple", "Klar · zeitlos · reduziert", "#91C9BD", "#182320", "#F0F7F4"),
        new("Dark", "Graphit · dezentes Eisblau", "#91B8D7", "#111820", "#F0F4F8")
    };
    public static Theme Get(string name) => All.FirstOrDefault(t => t.Name == name) ?? All[0];
}


