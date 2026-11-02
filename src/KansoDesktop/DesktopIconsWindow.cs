using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace KansoDesktop;
// Only allocate a transparent surface around actual icons, never an empty full monitor.
internal sealed class DesktopIconsWindow : Window
{
    private readonly Controller _controller;
    private readonly Forms.Screen _screen;
    private readonly IntPtr _host;
    private readonly Canvas _canvas = new() { ClipToBounds = true };
    private int _generation;
    private bool _closed;
    internal DesktopIconsWindow(Controller controller, Forms.Screen screen, IntPtr host)
    {
        _controller = controller; _screen = screen; _host = host;
        Title = "Kanso · Desktop-Symbole · "+(screen.Primary?"Hauptmonitor":screen.DeviceName); WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = controller.DiagnosticTaskbarWindows; ShowActivated = false; ResizeMode = ResizeMode.NoResize;
        Left = screen.Bounds.Left; Top = screen.Bounds.Top; Width = 1; Height = 1; Content = _canvas;
        Closed += (_, _) => { _closed = true; _generation++; };
        Loaded += (_, _) => { if (host != IntPtr.Zero) DesktopShell.Attach(this, host, screen.Bounds.Left, screen.Bounds.Top); RefreshItems(); };
        // DpiChanged bubbles from newly attached children too, even at 96 -> 96.
        // Rebuilding for those notifications creates an endless clear/add loop.
        DpiChanged += (_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, this) || e.OldDpi.Equals(e.NewDpi)) return;
            _controller.TraceInteraction($"loose window dpi {_screen.DeviceName} {e.OldDpi.PixelsPerDip}->{e.NewDpi.PixelsPerDip}");
            _controller.RefreshItems();
        };
    }
    internal async void RefreshItems()
    {
        if (_closed) return;
        if (_controller.InteractionActive) { _controller.RefreshItems(); return; }
        int generation = ++_generation;
        _controller.TraceInteraction($"loose refresh {_screen.DeviceName} generation={generation}");
        try
        {
            var icons = _controller.LooseIcons().Select(i => new IconPoint(i.Path, i.X, i.Y)).ToArray();
            var layout = IconLayout.Arrange(_screen.Bounds, _screen.WorkingArea, _screen.Primary, icons);
            double scale = Math.Max(.5, DesktopShell.GetDpiForWindow(new WindowInteropHelper(this).Handle) / 96.0);
            // Construct off-tree: an async yield must never expose a half-built layer,
            // or remove the tile that just received a click/menu/drag.
            var tiles = new List<UIElement>();
            double width = layout.Bounds.Width / scale, height = layout.Bounds.Height / scale;
            int count = 0;
            foreach (var icon in layout.Items)
            {
                if (_closed || generation != _generation) return;
                var item = _controller.Items.FirstOrDefault(i => string.Equals(i.Path, icon.Path, StringComparison.OrdinalIgnoreCase)) ?? new DesktopItem(icon.Path, "System", true);
                if (icon.Path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
                {
                    var native = _controller.LooseIcons().First(i => i.Path == icon.Path); item = item with { Name = native.Name };
                }
                var tile = DesktopTile.Create(_controller, item, true); Canvas.SetLeft(tile, (icon.X - layout.Bounds.Left) / scale); Canvas.SetTop(tile, (icon.Y - layout.Bounds.Top) / scale); tiles.Add(tile);
                if (++count % 24 == 0) await Dispatcher.Yield(DispatcherPriority.Background);
            }
            if (_closed || generation != _generation) return;
            if (_controller.InteractionActive) { _controller.RefreshItems(); return; }
            _canvas.Children.Clear();
            if (Math.Abs(Width - width) > .1) Width = width; if (Math.Abs(Height - height) > .1) Height = height;
            if (_host != IntPtr.Zero) DesktopShell.Position(this, _host, layout.Bounds.Left, layout.Bounds.Top);
            foreach (var tile in tiles) _canvas.Children.Add(tile);
        }
        catch (Exception ex) { _controller.StopDesktop(); _controller.Report("Desktop-Ansicht wurde sicher zurückgesetzt.", ex); }
    }
}

