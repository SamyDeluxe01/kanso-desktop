using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KansoDesktop;

internal static class DesktopTile
{
    // Layered Windows hit testing ignores fully transparent pixels. Keep the complete
    // tile clickable, including transparent holes in app artwork and space around labels.
    private static readonly Brush IdleFill = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
    internal static Border Create(Controller c, DesktopItem item, bool loose = false)
    {
        var s = c.State;
        var content = new StackPanel { Width = loose ? 64 : 88, IsHitTestVisible = false };
        var iconBox = new Image { Width = s.IconSize, Height = s.IconSize, Margin = new Thickness(0, 0, 0, 7), Source = ShellIcons.Fallback };
        content.Children.Add(iconBox);
        var name = Ui.Text(item.Name, 11, item.Missing ? "#E6A6A6" : Themes.Get(s.Theme).Text);
        name.TextAlignment = TextAlignment.Center;
        name.MaxHeight = 36;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        content.Children.Add(name);
        var tile = new Border
        {
            Child = content,
            Width = loose ? 76 : 108,
            CornerRadius = new CornerRadius(7),
            Background = IdleFill,
            Focusable = true,
            Padding = loose ? new Thickness(3, 6, 3, 4) : new Thickness(7, 10, 7, 4),
            Margin = loose ? new Thickness(0) : new Thickness(0, 0, 8, 8),
            ToolTip = item.Missing ? L.T("Ziel nicht erreichbar: ") + item.Path : item.Path
        };
        System.Windows.Automation.AutomationProperties.SetName(tile, item.Name);
        bool requested = false, armed = false, dragging = false;
        Point start = new();
        tile.Loaded += async (_, _) =>
        {
            if (requested) return;
            requested = true;
            try { var icon = await ShellIcons.GetAsync(item); if (tile.IsLoaded) iconBox.Source = icon; }
            catch (Exception ex) { Diagnostics.Write("Symbol laden", ex); }
        };
        tile.MouseEnter += (_, _) => { if (!tile.IsKeyboardFocusWithin) tile.Background = Ui.Brush(s.Accent, .12); };
        tile.MouseLeave += (_, _) => { if (!tile.IsKeyboardFocusWithin) tile.Background = IdleFill; };
        tile.GotKeyboardFocus += (_, _) => tile.Background = Ui.Brush(s.Accent, .23);
        tile.LostKeyboardFocus += (_, _) => tile.Background = IdleFill;
        tile.PreviewMouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            tile.Focus();
            if (e.ClickCount == 2) { armed = false; c.Open(item); return; }
            start = e.GetPosition(tile); armed = true; tile.CaptureMouse();
        };
        tile.PreviewMouseLeftButtonUp += (_, e) => { armed = false; if (tile.IsMouseCaptured) tile.ReleaseMouseCapture(); e.Handled = true; };
        tile.LostMouseCapture += (_, _) => { if (!dragging) armed = false; };
        tile.PreviewMouseMove += (_, e) =>
        {
            if (!armed || dragging || e.LeftButton != MouseButtonState.Pressed) return;
            var point = e.GetPosition(tile);
            if (Math.Abs(point.X - start.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            armed = false; dragging = true;
            tile.ReleaseMouseCapture(); c.DragSession(true);
            bool cancelled = false;
            QueryContinueDragEventHandler track = (_, args) => { if (args.EscapePressed || args.Action == DragAction.Cancel) cancelled = true; };
            GiveFeedbackEventHandler feedback = (_, args) =>
            {
                var cursor = System.Windows.Forms.Cursor.Position;
                // Explorer cannot consume the private KansoItem format. Its None effect
                // does not mean our reference-position drop on the desktop is forbidden.
                bool desktopDrop = args.Effects == DragDropEffects.None && !cancelled && DesktopShell.IsDesktopBackground(cursor.X, cursor.Y);
                args.UseDefaultCursors = !desktopDrop;
                if (desktopDrop) Mouse.SetCursor(Cursors.Arrow);
                args.Handled = true;
            };
            tile.QueryContinueDrag += track;
            tile.GiveFeedback += feedback;
            try
            {
                var data = new DataObject(); data.SetData("KansoItem", item);
                var effect = DragDrop.DoDragDrop(tile, data, DragDropEffects.Link | DragDropEffects.Move);
                var cursor = System.Windows.Forms.Cursor.Position;
                if (effect == DragDropEffects.None && !cancelled && Mouse.LeftButton == MouseButtonState.Released && DesktopShell.IsDesktopBackground(cursor.X, cursor.Y)) c.DropOnDesktop(item, cursor.X - 36, cursor.Y - 20);
            }
            finally { tile.QueryContinueDrag -= track; tile.GiveFeedback -= feedback; Mouse.SetCursor(null); dragging = false; c.DragSession(false); }
        };
        void OpenMenu(bool keyboard = false)
        {
            c.MenuInteraction(true);
            try
            {
                armed = false;
                if (tile.IsMouseCaptured) tile.ReleaseMouseCapture();
                tile.Focus();
                if (tile.IsLoaded && PresentationSource.FromVisual(tile) != null) ShellContextMenu.Open(tile, item.Path, keyboard);
            }
            catch (Exception ex) { c.Report(L.T("Windows-Kontextmenü konnte nicht geöffnet werden."), ex); }
            finally { c.MenuInteraction(false); }
        }
        tile.ContextMenuOpening += (_, e) => e.Handled = true;
        tile.PreviewMouseRightButtonUp += (_, e) => { e.Handled = true; OpenMenu(); };
        tile.KeyDown += (_, e) => { if (e.Key == Key.Enter) { c.Open(item); e.Handled = true; } else if (e.Key == Key.Apps || (e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift)) { OpenMenu(true); e.Handled = true; } };
        return tile;
    }
}


