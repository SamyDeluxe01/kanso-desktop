using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace KansoDesktop;

internal static class L
{
    internal sealed record Language(string Code, string Name);
    internal static readonly Language[] Languages = {
        new("de", "Deutsch"), new("en", "English"), new("fr", "Français"),
        new("es", "Español"), new("it", "Italiano"), new("pt", "Português"), new("ja", "日本語")
    };
    private static readonly Dictionary<string, string[]> Catalog = ReadCatalog();
    internal static string Current { get; private set; } = "de";
    internal static string Normalize(string? code) => Languages.Any(l => l.Code == code) ? code! : "de";
    internal static void Set(string? code) => Current = Normalize(code);
    internal static string T(string original)
    {
        if (Current == "de" || !Catalog.TryGetValue(original, out var translations)) return original;
        int index = Array.FindIndex(Languages, l => l.Code == Current) - 1;
        return index >= 0 && index < translations.Length ? translations[index] : original;
    }
    internal static string F(string original, params object[] values) => string.Format(CultureInfo.GetCultureInfo(Current), T(original), values);
    internal static int TranslationCount => Catalog.Count;
    internal static IEnumerable<KeyValuePair<string, string[]>> Entries => Catalog;
    private static Dictionary<string, string[]> ReadCatalog()
    {
        using var stream = typeof(L).Assembly.GetManifestResourceStream("Kanso.Translations")
            ?? throw new InvalidDataException("Missing Kanso translation resource.");
        var catalog = JsonSerializer.Deserialize<Dictionary<string, string[]>>(stream)
            ?? throw new InvalidDataException("Invalid Kanso translation resource.");
        if (catalog.Values.Any(v => v.Length != Languages.Length - 1 || v.Any(string.IsNullOrWhiteSpace)))
            throw new InvalidDataException("Incomplete Kanso translations.");
        return catalog;
    }
}
