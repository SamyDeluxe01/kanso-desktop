using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace KansoDesktop;

public sealed record IconPoint(string Path, int X, int Y);
public sealed record IconPlacement(string Path, int X, int Y);
public sealed record IconRegion(Rectangle Bounds, IReadOnlyList<IconPlacement> Items);

// Pure layout logic: coordinates in physical pixels; no files, Shell calls or UI side effects.
public static class IconLayout
{
    public static IconRegion Arrange(Rectangle screen, Rectangle work, bool primary, IReadOnlyList<IconPoint> icons)
    {
        var occupied = icons.Where(i => i.X != int.MinValue).Select(i => new Rectangle(i.X, i.Y, 76, 100)).ToList();
        var placed = new List<IconPlacement>(); int fallback = 0;
        foreach (var icon in icons)
        {
            int x = icon.X, y = icon.Y;
            if (x == int.MinValue)
            {
                if (!primary) continue;
                int rows = Math.Max(1, (work.Height - 24) / 100); bool found = false;
                while (fallback < 3000)
                {
                    x = work.Left + 12 + (fallback / rows) * 88; y = work.Top + 12 + (fallback % rows) * 100; fallback++;
                    var cell = new Rectangle(x, y, 76, 96);
                    if (cell.Right > work.Right) break;
                    if (cell.Bottom <= work.Bottom && !occupied.Any(r => r.IntersectsWith(cell))) { occupied.Add(cell); found = true; break; }
                }
                if (!found) throw new InvalidOperationException("Für lose Symbole ist nicht genügend Platz. Die Windows-Ansicht wird wiederhergestellt.");
            }
            if (screen.Contains(x, y)) placed.Add(new(icon.Path, x, y));
        }
        if (placed.Count == 0) return new(new Rectangle(screen.Left, screen.Top, 1, 1), placed);
        int left = placed.Min(i => i.X), top = placed.Min(i => i.Y);
        int right = Math.Min(screen.Right, placed.Max(i => i.X) + 76), bottom = Math.Min(screen.Bottom, placed.Max(i => i.Y) + 100);
        return new(new Rectangle(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top)), placed);
    }
}

