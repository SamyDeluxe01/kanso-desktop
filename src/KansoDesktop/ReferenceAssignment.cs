using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KansoDesktop;

public static class ReferenceAssignment
{
    public static int Apply(Settings state, IEnumerable<string> input, FenceModel? target)
    {
        if (target != null && !state.Fences.Contains(target)) throw new InvalidOperationException(L.T("Die Zielgruppe existiert nicht mehr."));
        var paths = input.Where(p => !string.IsNullOrWhiteSpace(p) && !p.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var incoming = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        var owned = new HashSet<string>(state.Fences.SelectMany(f => f.Paths), StringComparer.OrdinalIgnoreCase);
        if (target != null && owned.Count + paths.Count(p => !owned.Contains(p)) > 20000) throw new InvalidDataException("Maximal 20.000 Zuordnungen werden unterstützt.");
        var current = target == null ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : new HashSet<string>(target.Paths, StringComparer.OrdinalIgnoreCase);
        var changes = paths.Where(p => target == null ? owned.Contains(p) : !current.Contains(p)).ToArray();
        if (changes.Length == 0) return 0;
        incoming = new(changes, StringComparer.OrdinalIgnoreCase);
        foreach (var group in state.Fences) group.Paths.RemoveAll(p => incoming.Contains(p));
        if (target != null) target.Paths.AddRange(changes);
        return changes.Length;
    }
}

