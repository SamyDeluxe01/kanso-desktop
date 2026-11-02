using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace KansoDesktop;

public sealed record DesktopPoint(int X, int Y);
public static class GroupPlacement
{
    public static double SnapHeader(double y, double visibleHeight, bool opensUp, Rectangle work, double scale)
    {
        scale = scale > 0 ? scale : 1;
        double gap = 12 * scale, reach = 32 * scale, title = 52 * scale;
        double windowTop = y - (opensUp ? (visibleHeight - 52) * scale : 0);
        double windowBottom = windowTop + visibleHeight * scale;
        double topDistance = Math.Abs(windowTop - (work.Top + gap));
        double bottomDistance = Math.Abs(windowBottom - (work.Bottom - gap));
        if (topDistance <= reach && topDistance <= bottomDistance) return work.Top + gap;
        if (bottomDistance <= reach) return work.Bottom - gap - title;
        return y;
    }
    // Coordinates are physical screen pixels; sizes and gaps are logical pixels.
    // Y always anchors the title, including while opening upwards.
    public static (double HeaderY, double Height, bool Up) FoldBounds(double y, double height, Rectangle work, double scale, bool snap)
    {
        scale = scale > 0 ? scale : 1;
        double top = work.Top + 12 * scale, bottom = work.Bottom - 12 * scale;
        double header = 52 * scale;
        y = Math.Clamp(y, top, Math.Max(top, bottom - header));
        if (snap && y - top <= 24 * scale) y = top;
        if (snap && bottom - header - y <= 24 * scale) y = bottom - header;
        bool up = y + height * scale > bottom && y - top > bottom - y - header;
        double available = up ? y + header - top : bottom - y;
        return (y, Math.Max(52, Math.Min(height, available / scale)), up);
    }
    public static DesktopPoint Reachable(double x, double y, double width, IReadOnlyList<Rectangle> monitors, Rectangle primary)
    {
        int px = double.IsFinite(x) ? (int)Math.Clamp(x, int.MinValue, int.MaxValue) : primary.Left + 80;
        int py = double.IsFinite(y) ? (int)Math.Clamp(y, int.MinValue, int.MaxValue) : primary.Top + 80;
        var title = new Rectangle(px, py, (int)Math.Clamp(width, 64, 1600), 48);
        if (monitors.Any(m => { var part = Rectangle.Intersect(m, title); return part.Width >= 64 && part.Height >= 24; })) return new(px, py);
        return new(primary.Left + Math.Min(80, Math.Max(0, primary.Width - 64)), primary.Top + Math.Min(80, Math.Max(0, primary.Height - 48)));
    }
}

public sealed class FoldBehavior
{
    public bool Auto { get; private set; }
    public bool Folded { get; private set; }
    public bool PointerInside { get; private set; }
    public bool Interacting { get; private set; }
    public bool MenuOpen { get; private set; }
    public bool MayFold => Auto && !PointerInside && !Interacting && !MenuOpen;
    public FoldBehavior(bool auto, bool savedCollapsed) { Auto = auto; Folded = auto || savedCollapsed; }
    public void Enter() { bool changed = !PointerInside; PointerInside = true; if (Auto && changed) Folded = false; }
    public void Leave() => PointerInside = false;
    public void Interaction(bool active) => Interacting = active;
    public void Menu(bool open) => MenuOpen = open;
    public void SetAuto(bool enabled) { Auto = enabled; if (!enabled) Folded = false; }
    public void SetFolded(bool folded) => Folded = folded;
    public void Toggle() => Folded = !Folded;
    public void Expire() { if (MayFold) Folded = true; }
}

