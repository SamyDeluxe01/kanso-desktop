using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace KansoDesktop;

internal sealed class FenceWindow : Window
{
    private readonly Controller _controller;
    private readonly FenceModel? _assignment;
    private readonly FenceModel _model;
    private readonly Border _panel;
    private readonly TextBlock _empty;
    private int _itemCount;
    private readonly TextBlock _title;
    private readonly Button _collapse;
    private readonly Grid _root;
    private readonly ListBox _scroll;
    private readonly Thumb _resize;
    private IntPtr _host;
    private bool _dragging;
    private readonly FoldBehavior _fold;
    private bool _folded { get => _fold.Folded; set => _fold.SetFolded(value); }
    private bool _resizing;
    private readonly DispatcherTimer _leave = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private System.Drawing.Point _dragOrigin;
    private double _startX, _startY;
    private bool _dragOpenedUp;
    private double _dragHeight;
    private int _renderRevision, _animationRevision;
    private bool _up, _closed;
    private double _openHeight;
    private Grid _header = null!;
    private IDataObject? _dragData;
    private string[] _dragPaths = Array.Empty<string>();
    private readonly List<DispatcherTimer> _pendingDrops = new();

    internal FenceWindow(Controller controller, FenceModel? model)
    {
        _controller = controller; _assignment = model; _model = model ?? controller.State.Inbox;
        _fold = new FoldBehavior(controller.State.AutoCollapse, _model.Collapsed);
        Title = "Kanso · " + _model.Title; Width = _model.Width; Height = _folded ? 52 : _model.Height;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; ResizeMode = ResizeMode.NoResize; ShowActivated = false;
        Left = _model.X; Top = _model.Y;
        _root = new Grid { ClipToBounds = true };
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(50) });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = new Grid { Background = Brushes.Transparent, Cursor = Cursors.SizeAll, Margin = new Thickness(16, 0, 10, 0) };
        _header = header; header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _title = Ui.Text(_model.Title, 13); _title.VerticalAlignment = VerticalAlignment.Center; _title.Margin = new Thickness(0);
        header.Children.Add(_title);
        _collapse = Ui.Button("⌃", ToggleCollapse); _collapse.Margin = new Thickness(0); _collapse.Padding = new Thickness(0); _collapse.Width = 28; _collapse.Height = 28; _collapse.HorizontalContentAlignment = HorizontalAlignment.Center; _collapse.VerticalContentAlignment = VerticalAlignment.Center; _collapse.Background = Brushes.Transparent; _collapse.FontSize = 16;
        _collapse.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(_collapse, 1); header.Children.Add(_collapse);
        _collapse.ToolTip = L.T("Ein-/ausklappen (auch per Doppelklick auf den Titel)");
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (_collapse.IsMouseOver) return;
            if (e.ClickCount == 2) { ToggleCollapse(); return; }
            _dragging = true; _dragOrigin = System.Windows.Forms.Cursor.Position;
            _dragOpenedUp = _up; _dragHeight = Height;
            var handle = new WindowInteropHelper(this).Handle; DesktopShell.GetWindowRect(handle, out var rect);
            _startX = _model.X; _startY = _model.Y; _fold.Interaction(true); _leave.Stop(); header.CaptureMouse();
        };
        header.MouseMove += (_, _) =>
        {
            if (!_dragging) return;
            var p = System.Windows.Forms.Cursor.Position;
            _model.X = _startX + p.X - _dragOrigin.X; _model.Y = _startY + p.Y - _dragOrigin.Y;
            var work = System.Windows.Forms.Screen.FromPoint(p).WorkingArea;
            double snapped = GroupPlacement.SnapHeader(_model.Y, _dragHeight, _dragOpenedUp, work, Scale);
            _model.Y = snapped; ConfigurePlacement(false); UpdateChevron();
            Reposition();
        };
        header.AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler((_, e) => { if (e.ChangedButton == MouseButton.Left) FinishHeaderDrag(); }), true);
        header.LostMouseCapture += (_, _) => FinishHeaderDrag();
        _root.Children.Add(header);
        _scroll = IconRowConverter.Create(controller); Grid.SetRow(_scroll, 1); _root.Children.Add(_scroll);
        _empty = Ui.Text(L.T("Dateien oder Ordner hierher ziehen.\nDie Originale bleiben am bisherigen Ort."), 12, "#C4B6CA"); _empty.Margin = new Thickness(16, 4, 16, 12); Grid.SetRow(_empty, 1); _root.Children.Add(_empty);
        _resize = new Thumb
        {
            Width = 18,
            Height = 18,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Cursor = Cursors.SizeNWSE,
            Opacity = 0
        };
        Grid.SetRow(_resize, 1); _root.Children.Add(_resize);
        _resize.DragStarted += (_, _) => { _resizing = true; _fold.Interaction(true); _leave.Stop(); ++_animationRevision; BeginAnimation(HeightProperty, null); ConfigurePlacement(false); Height = _openHeight; ScrollViewer.SetVerticalScrollBarVisibility(_scroll, ScrollBarVisibility.Auto); };
        _resize.DragDelta += (_, e) =>
        {
            if (_folded) return;
            Width = Math.Clamp(Width + e.HorizontalChange, 240, 1600); Height = Math.Clamp(Height + (_up ? -e.VerticalChange : e.VerticalChange), 150, 1800);
            _model.Width = Width; _model.Height = Height; ConfigurePlacement(false); Height = _openHeight; Reposition();
        };
        _resize.DragCompleted += (_, _) => { _resizing = false; _fold.Interaction(false); controller.Save(); ScheduleFold(); };
        _panel = new Border { Child = _root, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(16), Padding = new Thickness(1) };
        Content = _panel;
        AllowDrop = true;
        PreviewDragOver += (_, e) => { e.Effects = PrepareDrop(e.Data, e.AllowedEffects); e.Handled = true; };
        PreviewDrop += (_, e) =>
        {
            e.Handled = true; e.Effects = PrepareDrop(e.Data, e.AllowedEffects);
            _controller.TraceInteraction($"drop received effect={e.Effects} count={_dragPaths.Length}");
            if (e.Effects != DragDropEffects.None) QueueDropPaths(_dragPaths.ToArray());
            else { _controller.Report(L.T("Ablegen nicht möglich: Das Element ist in diesem Modus nicht verfügbar.")); _fold.Interaction(false); ScheduleFold(); }
            _dragData = null; _dragPaths = Array.Empty<string>();
        };
        _leave.Tick += (_, _) =>
        {
            _leave.Stop(); if (IsMouseOver) { _fold.Enter(); return; }
            _fold.Leave(); _fold.Interaction(_dragging || _resizing || IsMouseCaptureWithin || controller.DragActive); _fold.Expire(); SetFolded(_fold.Folded);
        };
        MouseEnter += (_, _) => { _leave.Stop(); if (!_dragging) { _fold.Enter(); SetFolded(_fold.Folded); } };
        MouseLeave += (_, _) => { _fold.Leave(); ScheduleFold(); };
        ContextMenuOpening += (_, _) => { _fold.Menu(true); _leave.Stop(); };
        ContextMenuClosing += (_, _) => { _fold.Menu(false); ScheduleFold(); };
        Closed += (_, _) => { _closed = true; _leave.Stop(); _renderRevision++; _animationRevision++; foreach (var timer in _pendingDrops) timer.Stop(); _pendingDrops.Clear(); _dragData = null; _dragPaths = Array.Empty<string>(); };
        PreviewDragEnter += (_, e) => { e.Effects = PrepareDrop(e.Data, e.AllowedEffects); e.Handled = true; _controller.TraceInteraction($"drop enter effect={e.Effects} count={_dragPaths.Length}"); if (e.Effects != DragDropEffects.None) { _leave.Stop(); _fold.Interaction(true); SetFolded(false); } };
        DragLeave += (_, _) => { _dragData = null; _dragPaths = Array.Empty<string>(); _controller.TraceInteraction("drop leave"); _fold.Interaction(false); ScheduleFold(); };
        // Keep saved positions reachable after a monitor was removed. No persistent system changes.
        Loaded += (_, _) => { EnsureVisible(); ConfigurePlacement(false); SetFolded(_folded); Reposition(); };
        // Reflow virtual rows only on width changes; no native positioning from layout callbacks.
        SizeChanged += (_, e) => { if (_up) MoveAnchor(); if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 2) RefreshItems(); };
        ApplyAppearance(); RefreshItems();
    }

    private void FinishHeaderDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        if (_header.IsMouseCaptured) _header.ReleaseMouseCapture();
        var work = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
        _model.Y = GroupPlacement.SnapHeader(_model.Y, Height, _up, work, Scale);
        ConfigurePlacement(true); SetFolded(_folded); _fold.Interaction(false);
        _controller.TraceInteraction($"dock finish up={_up}"); _controller.Save(); ScheduleFold();
    }

    internal static string[] ReadDropPaths(IDataObject data)
    {
        if (data.GetDataPresent("KansoItem", false) && data.GetData("KansoItem", false) is DesktopItem item) return new[] { item.Path };
        return data.GetDataPresent(DataFormats.FileDrop, true) && data.GetData(DataFormats.FileDrop, true) is string[] paths ? paths.ToArray() : Array.Empty<string>();
    }
    private DragDropEffects PrepareDrop(IDataObject data, DragDropEffects allowed)
    {
        try
        {
            // Materialize delayed Shell data while the drag source is still available.
            if (!ReferenceEquals(data, _dragData))
            {
                _dragPaths = ReadDropPaths(data); _dragData = data;
                _controller.TraceInteraction($"drop data count={_dragPaths.Length} eligible={_controller.CanAcceptDrop(_dragPaths, _assignment)} allowed={allowed}");
            }
            return _controller.CanAcceptDrop(_dragPaths, _assignment) ? ReferenceDropEffect(allowed) : DragDropEffects.None;
        }
        catch (Exception ex) { _dragData = null; _dragPaths = Array.Empty<string>(); _controller.TraceInteraction($"drop read failed hr={ex.HResult:X8}"); return DragDropEffects.None; }
    }

    internal void QueueDrop(IDataObject data)
    {
        // Snapshot before returning to Explorer; never retain an OLE data object.
        try
        {
            QueueDropPaths(ReadDropPaths(data));
        }
        catch (Exception ex) { _fold.Interaction(false); ScheduleFold(); _controller.Report(L.T("Ablegen konnte nicht gelesen werden."), ex); }
    }
    private void QueueDropPaths(string[] paths)
    {
        // A posted callback can run inside OLE's nested message pump. A timer gives
        // the source a turn to complete Drop before synchronous native confirmation.
        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
        _pendingDrops.Add(timer);
        timer.Tick += (_, _) =>
        {
            timer.Stop(); _pendingDrops.Remove(timer);
            try
            {
                if (_closed || !_controller.CanAcceptDrop(paths, _assignment)) { _controller.TraceInteraction("drop rejected before commit"); return; }
                _controller.TraceInteraction($"drop commit count={paths.Length}");
                _controller.AssignPaths(paths, _assignment);
                bool committed = _assignment != null && paths.All(p => _assignment.Paths.Contains(p, StringComparer.OrdinalIgnoreCase));
                _controller.TraceInteraction($"drop committed={committed}");
                if (committed) _controller.Report(L.T("Datei zur Gruppe hinzugefügt."));
            }
            catch (Exception ex) { _controller.Report(L.T("Ablegen konnte nicht abgeschlossen werden."), ex); }
            finally { _fold.Interaction(false); ScheduleFold(); }
        };
        timer.Start();
    }
    internal void ReceiveDrop(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent("KansoItem", false) && data.GetData("KansoItem", false) is DesktopItem item) _controller.Assign(item, _assignment);
            else if (data.GetDataPresent(DataFormats.FileDrop, false) && data.GetData(DataFormats.FileDrop, false) is string[] paths) _controller.AssignPaths(paths, _assignment);
        }
        catch (Exception ex) { _controller.Report(L.T("Ablegen konnte nicht abgeschlossen werden."), ex); }
        finally { _fold.Interaction(false); ScheduleFold(); }
    }
    internal static DragDropEffects ReferenceDropEffect(DragDropEffects allowed) => (allowed & DragDropEffects.Link) != 0 ? DragDropEffects.Link : (allowed & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : DragDropEffects.None;
    internal static DragDropEffects ReferenceDropEffect(IDataObject data, DragDropEffects allowed)
    {
        // System icons may be positioned on the desktop, but are not filesystem
        // references and must not promise a successful group assignment.
        if (data.GetDataPresent("KansoItem", false))
            return data.GetData("KansoItem", false) is DesktopItem item && !item.Path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ? ReferenceDropEffect(allowed) : DragDropEffects.None;
        return data.GetDataPresent(DataFormats.FileDrop, false) ? ReferenceDropEffect(allowed) : DragDropEffects.None;
    }
    internal int DisplayedCount => _itemCount;
    internal int RealizedTileCount => System.Linq.Enumerable.Range(0, _scroll.Items.Count).Where(i => _scroll.ItemContainerGenerator.ContainerFromIndex(i) != null).Sum(i => (_scroll.Items[i] as DesktopItem[])?.Length ?? 0);
    internal void Attach(IntPtr host)
    {
        _host = host; DesktopShell.Attach(this, host, _model.X, _model.Y);
    }
    private double Scale => (DesktopShell.GetDpiForWindow(new WindowInteropHelper(this).Handle) is uint dpi && dpi > 0 ? dpi : 96) / 96.0;
    private double WindowY => _model.Y - (_up ? (Height - 52) * Scale : 0);
    private void Reposition() { if (IsLoaded) DesktopShell.Position(this, _host, _model.X, WindowY); }
    private void MoveAnchor()
    {
        if (!IsLoaded) return;
        // Move only: feeding an animated size back into native layout can cause jitter.
        DesktopShell.SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero, (int)Math.Round(_model.X), (int)Math.Round(WindowY), 0, 0, 0x4215);
    }
    private void ConfigurePlacement(bool snap)
    {
        var work = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)_model.X, (int)_model.Y)).WorkingArea;
        double scale = IsLoaded ? Scale : 1;
        var placement = GroupPlacement.FoldBounds(_model.Y, _model.Height, work, scale, snap);
        _model.Y = placement.HeaderY; _openHeight = placement.Height; _up = placement.Up;
        _root.RowDefinitions[0].Height = _up ? new GridLength(1, GridUnitType.Star) : new GridLength(50);
        _root.RowDefinitions[1].Height = _up ? new GridLength(50) : new GridLength(1, GridUnitType.Star);
        Grid.SetRow(_header, _up ? 1 : 0);
        Grid.SetRow(_scroll, _up ? 0 : 1); Grid.SetRow(_empty, _up ? 0 : 1); Grid.SetRow(_resize, _up ? 0 : 1);
        _resize.VerticalAlignment = _up ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        _resize.Cursor = _up ? Cursors.SizeNESW : Cursors.SizeNWSE;
    }
    private void UpdateChevron()
    {
        bool pointsUp = _folded ? _up : !_up;
        _collapse.Content = new System.Windows.Shapes.Path { Data = Geometry.Parse(pointsUp ? "M 0,4 L 4,0 8,4" : "M 0,0 L 4,4 8,0"), Stroke = _title.Foreground, StrokeThickness = 1.5, Width = 8, Height = 4, Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    }
    internal void DragSession(bool active) { _fold.Interaction(active); if (active) _leave.Stop(); else ScheduleFold(); }
    internal void MenuSession(bool open) { _fold.Menu(open); if (open) _leave.Stop(); else ScheduleFold(); }
    private void ScheduleFold() { if (!IsMouseOver) _fold.Leave(); if (_fold.MayFold) { _leave.Stop(); _leave.Start(); } }
    private void EnsureVisible()
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        var primary = System.Windows.Forms.Screen.PrimaryScreen ?? screens[0];
        var point = GroupPlacement.Reachable(_model.X, _model.Y, Width, Array.ConvertAll(screens, s => s.WorkingArea), primary.WorkingArea);
        if (_model.X != point.X || _model.Y != point.Y) { _model.X = point.X; _model.Y = point.Y; _controller.Save(); }
    }
    internal void ApplyAppearance()
    {
        var s = _controller.State;
        if (_fold.Auto != s.AutoCollapse) { _fold.SetAuto(s.AutoCollapse); SetFolded(_fold.Folded); }
        if (!s.Animations) SetFolded(_folded);
        try { _panel.Background = Ui.Brush(s.Panel, s.PanelOpacity); _panel.BorderBrush = Ui.Brush(s.Accent, .4); }
        catch { _panel.Background = Ui.Brush("#17141D", .55); _panel.BorderBrush = Ui.Brush("#DFA0B6", .4); }
        _title.Foreground = Ui.Brush(Themes.Get(s.Theme).Text);
        UpdateChevron();
        _scroll.Visibility = _folded ? Visibility.Collapsed : Visibility.Visible;
        _resize.Visibility = _folded ? Visibility.Collapsed : Visibility.Visible;
        _empty.Visibility = !_folded && _itemCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshItems();
    }
    private void ToggleCollapse()
    {
        _model.Collapsed = !_folded; SetFolded(_model.Collapsed); _controller.Save();
    }
    internal void SetFolded(bool folded)
    {
        ConfigurePlacement(false);
        _folded = folded;
        double destination = folded ? 52 : _openHeight;
        double current = Height; int revision = ++_animationRevision;
        ScrollViewer.SetVerticalScrollBarVisibility(_scroll, ScrollBarVisibility.Hidden);
        BeginAnimation(HeightProperty, null); Height = destination;
        UpdateChevron();
        _scroll.Visibility = folded ? Visibility.Collapsed : Visibility.Visible;
        _resize.Visibility = folded ? Visibility.Collapsed : Visibility.Visible;
        _empty.Visibility = !folded && _itemCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_controller.State.Animations && SystemParameters.ClientAreaAnimation && Math.Abs(current - destination) > .5)
        {
            var animation = new DoubleAnimation(current, destination, TimeSpan.FromMilliseconds(280)) { EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
            animation.Completed += (_, _) => { if (revision == _animationRevision && !_closed) { ScrollViewer.SetVerticalScrollBarVisibility(_scroll, ScrollBarVisibility.Auto); Reposition(); } };
            BeginAnimation(HeightProperty, animation);
        }
        else ScrollViewer.SetVerticalScrollBarVisibility(_scroll, ScrollBarVisibility.Auto);
        Reposition();
    }

    internal void RefreshItems()
    {
        int revision = ++_renderRevision;
        var items = System.Linq.Enumerable.ToArray(_controller.ItemsFor(_assignment));
        var scroller = FindScroll(_scroll); int offset = (int)(scroller?.VerticalOffset ?? 0);
        string? anchor = offset < _scroll.Items.Count && _scroll.Items[offset] is DesktopItem[] oldRow ? oldRow.FirstOrDefault()?.Path : null;
        _itemCount = items.Length; _title.Text = _model.Title;
        int columns = Math.Max(1, (int)Math.Floor((Width - 20) / 116)); var rows = items.Chunk(columns).ToArray();
        _scroll.ItemsSource = rows; _empty.Visibility = !_folded && items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (anchor != null) { var row = rows.FirstOrDefault(r => r.Any(i => string.Equals(i.Path, anchor, StringComparison.OrdinalIgnoreCase))); if (row != null) Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => { if (revision == _renderRevision) _scroll.ScrollIntoView(row); }); }
    }
    private static ScrollViewer? FindScroll(DependencyObject element)
    {
        if (element is ScrollViewer scroll) return scroll;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++) { var found = FindScroll(VisualTreeHelper.GetChild(element, i)); if (found != null) return found; }
        return null;
    }
}




