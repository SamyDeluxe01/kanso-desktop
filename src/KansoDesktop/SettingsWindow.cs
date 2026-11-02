using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace KansoDesktop;

internal sealed class SettingsWindow : Window
{
    private readonly Controller _c;
    private readonly StackPanel _page;
    private readonly TextBlock _heading;
    private readonly TextBlock _status;
    private int _section;
    private TextBlock? _counts;
    private Button? _desktopButton;
    internal int CurrentSection => _section;
    internal SettingsWindow(Controller c)
    {
        _c = c; Title = "Kanso Desktop 1.1.0"; Width = 1040; Height = 780; MinWidth = 800; MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; FontFamily = new FontFamily("Segoe UI");
        Background = Ui.Brush("#16131E"); Foreground = Ui.Brush("#F2EDF5");
        var inputStyle = new Style(typeof(TextBox));
        inputStyle.Setters.Add(new Setter(Control.BackgroundProperty, Ui.Brush("#17131E")));
        inputStyle.Setters.Add(new Setter(Control.ForegroundProperty, Ui.Brush("#F2EDF5")));
        inputStyle.Setters.Add(new Setter(Control.BorderBrushProperty, Ui.Brush("#54405E")));
        Resources.Add(typeof(TextBox), inputStyle);
        var grid = new Grid { Background = Ui.Brush("#16131E") }; grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
        var nav = Ui.Stack(Ui.Text("K A N S O", 25), Ui.Text("DESKTOP / 1.1.0", 10, "#AD9DB5")); nav.Margin = new Thickness(22, 32, 20, 20);
        string[] labels = { L.T("Übersicht"), L.T("Meine Gruppen"), L.T("Design & Verhalten"), L.T("Daten & Ablage") };
        for (int i = 0; i < labels.Length; i++) { int index = i; nav.Children.Add(Ui.Button(labels[i], () => ShowSection(index))); }
        nav.Children.Add(Ui.Text(L.T("\nWeniger Unruhe.\nMehr dein Desktop."), 13, "#AD9DB5"));
        nav.Children.Add(Ui.Text(L.T("Sprache / Language"), 11, "#AD9DB5"));
        var language = new ComboBox { ItemsSource = L.Languages, DisplayMemberPath = "Name", SelectedValuePath = "Code", SelectedValue = _c.State.Language, FontSize = 13, Padding = new Thickness(6), Margin = new Thickness(0, 0, 0, 14), MinHeight = 32 };
        language.ToolTip = L.T("Die Sprache wird sofort angewendet und gespeichert.");
        language.SelectionChanged += (_, _) => { if (language.SelectedValue is string code && code != _c.State.Language) Dispatcher.BeginInvoke(() => _c.SetLanguage(code)); };
        nav.Children.Add(language);
        nav.Children.Add(Ui.Button(L.T("Desktop aktivieren"), c.StartDesktop));
        nav.Children.Add(Ui.Button(L.T("Windows-Desktop zurück"), () => { c.StopDesktop(); ShowSection(_section); }));
        nav.Children.Add(Ui.Button(L.T("App beenden"), c.RequestExit));
        var sidebar = new Border { Background = Ui.Brush("#1C1826"), Child = nav }; grid.Children.Add(sidebar);
        var main = new Grid { Margin = new Thickness(32, 30, 32, 20) };
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); main.RowDefinitions.Add(new RowDefinition()); main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _heading = Ui.Text("", 28); main.Children.Add(_heading);
        _page = new StackPanel(); var scroll = new ScrollViewer { Content = _page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetRow(scroll, 1); main.Children.Add(scroll);
        _status = Ui.Text("", 11, "#AD9DB5"); Grid.SetRow(_status, 2); main.Children.Add(_status);
        Grid.SetColumn(main, 1); grid.Children.Add(main); Content = grid;
        c.StatusChanged += UpdateStatus; Closed += (_, _) => c.StatusChanged -= UpdateStatus;
        ShowSection(0);
    }
    internal void ShowSection(int section)
    {
        _section = section; _counts = null; _desktopButton = null; _page.Children.Clear(); _heading.Text = new[] { L.T("Dein Desktop. Neu geordnet."), L.T("Meine Gruppen"), L.T("Design & Verhalten"), L.T("Deine Daten bleiben deine.") }[section];
        _status.Text = (_c.DesktopMode ? L.T("● Desktop-Modus aktiv") : L.T("○ Vorschau · Originaldateien unverändert")) + "  ·  " + _c.LastError;
        if (section == 0) Overview(); else if (section == 1) Groups(); else if (section == 2) Appearance(); else Data();
    }
    private void UpdateStatus()
    {
        _status.Text = (_c.DesktopMode ? L.T("● Desktop-Modus aktiv") : _c.StartingDesktop ? L.T("◌ Desktop wird vorbereitet") : L.T("○ Desktop-Modus aus")) + (string.IsNullOrEmpty(_c.LastError) ? "" : "  ·  " + _c.LastError);
        if (_counts != null) _counts.Text = L.F("{0} Gruppen  /  {1} bekannte Elemente", _c.State.Fences.Count, _c.Items.Count);
        if (_desktopButton != null) { _desktopButton.Content = _c.DesktopMode ? L.T("Desktop-Modus beenden") : _c.StartingDesktop ? L.T("Wird vorbereitet …") : L.T("Desktop-Modus aktivieren"); _desktopButton.IsEnabled = !_c.StartingDesktop; }
    }
    private void Overview()
    {
        _page.Children.Add(Ui.Text(L.T("Transparente Gruppen für deine echten Dateien und Ordner. Keine Kopien. Keine zusätzlichen Verknüpfungen."), 15, "#BCAECA"));
        var preview = Ui.Card(Ui.Stack(Ui.Text(L.T("VORSCHAU / ZUM AUSPROBIEREN"), 10, "#DFA0B6"), Ui.Text(L.T("Design ansehen"), 23),
            Ui.Text(L.T("Ziehe echte Dateien und Ordner vom Desktop in eine Gruppe. Sie bleiben an ihrem ursprünglichen Speicherort, einschließlich OneDrive. Öffnen per Doppelklick; aus der Gruppe nehmen über das Elementmenü. In der Vorschau bleiben die normalen Windows-Symbole zusätzlich sichtbar.")),
            Ui.Button(L.T("Vorschau öffnen · Desktop-Modus verlassen"), () => { _c.Preview(); Activate(); })));
        _desktopButton = Ui.Button(L.T("Desktop-Modus aktivieren"), () => { if (_c.DesktopMode) _c.StopDesktop(); else _c.StartDesktop(); UpdateStatus(); }, true);
        _page.Children.Add(Ui.Card(Ui.Stack(Ui.Text(L.T("DEIN DESKTOP"), 10, "#DFA0B6"), Ui.Text(L.T("Alles an seinem Platz."), 21),
            #if NATIVE_PREVIEW
            Ui.Text(_c.NativePersonalEnabled ? L.T("Freie Symbole bleiben auf der vorhandenen Windows-Desktopfläche. Ziehe eigene Dateien und Ordner in eine Gruppe. Die Originale bleiben an ihrem Speicherort.") : "Im nativen Test bleiben freie Symbole auf der vorhandenen Windows-Desktopfläche. Kanso ergänzt ausschließlich die bisherigen Gruppen. Nur die drei künstlichen Testdateien dürfen zugeordnet werden."),
#else
            Ui.Text("Im Desktop-Modus zeigt Kanso die Symbole selbst: zugeordnete Elemente nur in ihrer Gruppe, übrige Symbole frei auf dem Desktop. Die Windows-Symbolansicht wird für diese Sitzung ausgeblendet. Alle Dateien bleiben am ursprünglichen Ort; OneDrive kann wie bisher synchronisieren."),
#endif
            #if NATIVE_PREVIEW
            Ui.Text(_c.NativePersonalEnabled ? L.T("Zuordnungen werden dauerhaft in dieser Version gespeichert. Beim Beenden werden die Windows-Symbole freigegeben. Windows-Start und automatische Wiederherstellung sind unten einstellbar.") : "Separate Testversion mit unveränderter Gestaltung. Persönliche Zuordnungen werden nicht geladen. Der Test endet spätestens nach zehn Minuten; die künstlichen Dateien bleiben erhalten.", 12, "#BCAECA"),
#else
            Ui.Text("Beta: Andere Desktop-Organizer zuerst pausieren. Ein separater Wächter stellt die Windows-Symbole beim App-Ende wieder her. Der Modus startet nicht automatisch mit Windows.", 12, "#BCAECA"),
#endif
            _desktopButton)));
        #if NATIVE_PREVIEW
        if (_c.NativePersonalEnabled)
        {
            var startup = new CheckBox { Content = L.T("Kanso mit Windows starten"), Foreground = Ui.Brush("#F2EDF5"), IsChecked = _c.State.StartWithWindows, Margin = new Thickness(0, 8, 0, 8) };
            var resume = new CheckBox { Content = L.T("Zuletzt aktiven Desktop-Modus automatisch wiederherstellen"), Foreground = Ui.Brush("#F2EDF5"), IsChecked = _c.State.ResumeDesktop, Margin = new Thickness(0, 8, 0, 8) };
            bool updating = false;
            startup.Click += (_, _) =>
            {
                if (updating) return;
                try { _c.SetWindowsStartup(startup.IsChecked == true); }
                catch (Exception ex) { updating = true; startup.IsChecked = _c.State.StartWithWindows; updating = false; _c.Report(L.T("Windows-Autostart konnte nicht geändert werden."), ex); }
            };
            resume.Click += (_, _) => { _c.State.ResumeDesktop = resume.IsChecked == true; _c.Save(); if (_c.State.ResumeDesktop) _c.TryResumeDesktop(DateTime.UtcNow); };
            _page.Children.Add(Ui.Card(Ui.Stack(Ui.Text(L.T("START & WIEDERHERSTELLUNG"), 10, "#DFA0B6"), startup, resume)));
        }
        #endif
        _page.Children.Add(preview);
        _counts = Ui.Text("", 12, "#AD9DB5"); _page.Children.Add(_counts); UpdateStatus();
    }
    private void Groups()
    {
        _page.Children.Add(Ui.Text(L.T("Ziehe Dateien und Ordner in eine Gruppe. Kanso speichert nur die Gruppenzuordnung. Der Speicherort bleibt unverändert."), 14, "#BCAECA"));
        _page.Children.Add(Ui.Button(L.T("+ Neue Gruppe"), () =>
        {
            var f = new FenceModel { Title = L.T("Neue Gruppe"), X = 160 + _c.State.Fences.Count * 35, Y = 180 + _c.State.Fences.Count * 30 };
            EditGroup(f, true);
        }, true));
        foreach (var f in _c.State.Fences.ToArray())
        {
            _page.Children.Add(Ui.Card(Ui.Stack(Ui.Text(f.Title, 20), Ui.Text(L.F("{0} Elemente · {1}", f.Paths.Count, L.T(f.Collapsed ? L.T("eingeklappt") : L.T("ausgeklappt"))), 12, "#AD9DB5"),
                Ui.Button(L.T("Name & Elemente bearbeiten"), () => EditGroup(f, false)),
                Ui.Button(L.T("Gruppe auflösen"), () =>
                {
                    if (MessageBox.Show(L.T("Nur die Gruppe auflösen? Nur die Gruppenzuordnungen werden entfernt. Originale werden nicht verändert."), L.T("Gruppe auflösen"), MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                    _c.RemoveGroup(f); ShowSection(1);
                }))));
        }
    }
    internal Window EditGroup(FenceModel f, bool fresh, bool modal = true)
    {
        var name = new TextBox { Text = f.Title, FontSize = 16, Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 18) };
        var choices = _c.Items.Select(item => new GroupChoice(item, f.Paths.Contains(item.Path, StringComparer.OrdinalIgnoreCase))).ToArray();
        var search = new TextBox { Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 10), ToolTip = L.T("Nach Name oder Pfad suchen") };
        var list = new ListBox { Background = Ui.Brush("#17131E"), BorderThickness = new Thickness(0), Foreground = Ui.Brush("#F2EDF5") };
        ScrollViewer.SetCanContentScroll(list, true);
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        list.ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><CheckBox IsChecked='{Binding Chosen, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}' Foreground='#F2EDF5' Padding='4,7' ToolTip='{Binding Path}'><TextBlock Text='{Binding Name}' TextTrimming='CharacterEllipsis'/></CheckBox></DataTemplate>");
        var count = Ui.Text("", 12, "#AD9DB5");
        void Filter() { var query = search.Text.Trim(); var shown = choices.Where(c => query.Length == 0 || c.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || c.Path.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray(); list.ItemsSource = shown; count.Text = L.F("{0} von {1} Elementen · Auswahl bleibt beim Suchen erhalten", shown.Length, choices.Length); }
        search.TextChanged += (_, _) => Filter(); Filter();
        var body = Ui.Stack(Ui.Text(L.T("Gruppe bearbeiten"), 24), Ui.Text(L.T("Name")), name, Ui.Text(L.T("Desktop-Elemente zuordnen"), 16), Ui.Text(L.T("Speicherort und OneDrive-Synchronisation bleiben unverändert."), 12, "#AD9DB5"), Ui.Text(L.T("Suchen"), 12), search, count);
        var footer = new WrapPanel { Margin = new Thickness(28, 12, 20, 16) };
        var layout = new DockPanel(); DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer);
        var content = new Grid { Margin = new Thickness(28, 20, 28, 0) };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); content.RowDefinitions.Add(new RowDefinition());
        content.Children.Add(body); Grid.SetRow(list, 1); content.Children.Add(list); layout.Children.Add(content);
        var dialog = Ui.Dialog(L.T("Kanso · Gruppe"), new Grid()); dialog.Content = layout; dialog.Owner = this;
        dialog.Resources.Add(typeof(TextBox), Resources[typeof(TextBox)]);
        footer.Children.Add(Ui.Button(L.T("Speichern"), () =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) return;
            try
            {
                _c.EditGroup(f, fresh, name.Text, choices.Select(c => c.Path), choices.Where(c => c.Chosen).Select(c => c.Path));
                dialog.Close(); ShowSection(1);
            }
            catch (Exception ex) { MessageBox.Show(dialog, ex.Message, L.T("Gruppe konnte nicht gespeichert werden")); }
        }, true));
        footer.Children.Add(Ui.Button(L.T("Abbrechen"), dialog.Close)); if (modal) dialog.ShowDialog(); else dialog.Show(); return dialog;
    }
    private sealed class GroupChoice
    {
        public string Name { get; }
        public string Path { get; }
        public bool Chosen { get; set; }
        public GroupChoice(DesktopItem item, bool chosen) { Name = item.Name + (item.Folder ? L.T("  / Ordner") : ""); Path = item.Path; Chosen = chosen; }
    }
    private void Appearance()
    {
        _page.Children.Add(Ui.Text(L.T("Wähle eine Basis und mache sie zu deiner eigenen."), 14, "#BCAECA"));
        var themeGrid = new WrapPanel();
        foreach (var theme in Themes.All)
        {
            var tile = Ui.Button(theme.Name + "\n" + L.T(theme.Subtitle), () =>
            {
                _c.State.Theme = theme.Name; _c.State.Accent = theme.Accent; _c.State.Panel = theme.Panel; _c.ApplyAppearance(); ShowSection(2);
            }); tile.Width = 300; tile.Height = 85; tile.Background = Ui.Brush(theme.Panel); tile.Foreground = Ui.Brush(theme.Accent); themeGrid.Children.Add(tile);
        }
        _page.Children.Add(Ui.Card(Ui.Stack(Ui.Text(L.T("Themes"), 20), themeGrid)));
        var alpha = new Slider { Minimum = 8, Maximum = 95, Value = _c.State.PanelOpacity * 100, Margin = new Thickness(0, 0, 0, 16) };
        var readout = Ui.Text(L.F("Hintergrund-Deckkraft: {0} %", Math.Round(alpha.Value)), 14);
        alpha.ValueChanged += (_, _) => { _c.State.PanelOpacity = alpha.Value / 100; readout.Text = L.F("Hintergrund-Deckkraft: {0} %", Math.Round(alpha.Value)); foreach (var w in _c.Windows) w.ApplyAppearance(); };
        alpha.PreviewMouseLeftButtonUp += (_, _) => _c.Save(); alpha.LostKeyboardFocus += (_, _) => _c.Save();
        var auto = new CheckBox { Content = L.T("Automatisch einklappen · Öffnen beim Darüberfahren"), IsChecked = _c.State.AutoCollapse, Foreground = Ui.Brush("#F2EDF5"), Margin = new Thickness(0, 12, 0, 12) };
        auto.Checked += (_, _) => { _c.State.AutoCollapse = true; _c.ApplyAppearance(); }; auto.Unchecked += (_, _) => { _c.State.AutoCollapse = false; foreach (var w in _c.Windows) w.SetFolded(false); _c.ApplyAppearance(); };
        var animations = new CheckBox { Content = L.T("Dezente Hover- und Klappanimationen"), IsChecked = _c.State.Animations, Foreground = Ui.Brush("#F2EDF5"), Margin = new Thickness(0, 12, 0, 20) };
        animations.Checked += (_, _) => { _c.State.Animations = true; _c.ApplyAppearance(); }; animations.Unchecked += (_, _) => { _c.State.Animations = false; _c.ApplyAppearance(); };
        _page.Children.Add(Ui.Card(Ui.Stack(Ui.Text(L.T("Verhalten"), 20), auto, animations)));
        var accent = new TextBox { Text = _c.State.Accent, Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 14) };
        var panel = new TextBox { Text = _c.State.Panel, Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 14) };
        var icon = new WrapPanel();
        foreach (int size in new[] { 24, 32, 36, 48, 64 }) icon.Children.Add(Ui.Button(size + " px", () => { _c.State.IconSize = size; _c.ApplyAppearance(); }, _c.State.IconSize == size));
        _page.Children.Add(Ui.Card(Ui.Stack(readout, alpha, Ui.Text(L.T("Symbolgröße")), icon, Ui.Text(L.T("Akzentfarbe (#RRGGBB)")), accent, Ui.Text(L.T("Rahmen-Hintergrund (#RRGGBB)")), panel,
            Ui.Button(L.T("Eigene Farben anwenden"), () =>
            {
                try
                {
                    if (!System.Text.RegularExpressions.Regex.IsMatch(accent.Text, "^#[0-9a-fA-F]{6}$") || !System.Text.RegularExpressions.Regex.IsMatch(panel.Text, "^#[0-9a-fA-F]{6}$")) throw new FormatException();
                    _c.State.Accent = accent.Text; _c.State.Panel = panel.Text; _c.ApplyAppearance();
                }
                catch { MessageBox.Show(L.T("Bitte Farben als #RRGGBB eingeben, zum Beispiel #DFA0B6.")); }
            }, true))));
    }
    private void Data()
    {
        _page.Children.Add(Ui.Card(Ui.Stack(Ui.Text(L.T("Lokal. Portabel. Nachvollziehbar."), 22),
            Ui.Text(L.T("Daten bleiben lokal. Kanso speichert Layout, Farben und Dateizuordnungen. Die Originale bleiben an ihrem Speicherort.")),
            Ui.Text(L.F("Einstellungen: {0}\nSicherung: {1}", _c.Store.FileName, _c.Store.FileName + ".bak"), 12, "#AD9DB5"),
            Ui.Button(L.T("Layout exportieren"), async () => { var d = new SaveFileDialog { Filter = "Kanso-Layout (*.json)|*.json", FileName = "kanso-layout.json" }; if (d.ShowDialog() == true) try { await _c.ExportAsync(d.FileName); } catch (Exception ex) { MessageBox.Show(ex.Message); } }),
            Ui.Button(L.T("Fehlerprotokoll öffnen"), () => { Directory.CreateDirectory(Diagnostics.Folder); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Diagnostics.Folder) { UseShellExecute = true }); }),
            Ui.Text(L.T("Ein Layout-Export enthält nur Einstellungen und Dateipfade. Sichere deine Originaldateien separat. Teile Exporte nur bewusst, da sie persönliche Dateipfade enthalten können."), 12, "#DFA0B6"))));
        _page.Children.Add(Ui.Card(Ui.Stack(Ui.Text(L.T("Zurück zum Windows-Desktop"), 22), Ui.Text(L.T("Dieser Knopf schließt die Gruppenfenster. Originaldateien bleiben unverändert am bisherigen Ort. Aus einer Gruppe nehmen entfernt nur die Zuordnung.")),
            Ui.Button(L.T("Windows-Symbole wiederherstellen"), () => { _c.StopDesktop(); ShowSection(3); }, true))));
        _page.Children.Add(Ui.Card(Ui.Stack(Ui.Text(L.T("Hinweise"), 20), Ui.Text(L.T("Direktes Umbenennen in Gruppen und die Positionierung am Loslasspunkt sind noch nicht enthalten. Das native Modul bleibt nach dem Beenden inaktiv bis zum regulären Explorer-Neustart geladen."), 12, "#BCAECA"))));
    }
}



