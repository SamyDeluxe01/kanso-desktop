using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;

namespace KansoDesktop;

internal sealed class IconRowConverter : IValueConverter
{
    private readonly Controller _controller;
    internal IconRowConverter(Controller controller) => _controller = controller;
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (value is DesktopItem[] items) foreach (var item in items) row.Children.Add(DesktopTile.Create(_controller, item));
        return row;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    internal static ListBox Create(Controller controller)
    {
        var list = new ListBox { Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(9, 0, 9, 12), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        ScrollViewer.SetCanContentScroll(list, true);
        VirtualizingPanel.SetIsVirtualizing(list, true); VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(Control.TemplateProperty, (ControlTemplate)XamlReader.Parse("<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ListBoxItem'><ContentPresenter HorizontalAlignment='Stretch'/></ControlTemplate>")));
        list.ItemContainerStyle = style;
        var root = new FrameworkElementFactory(typeof(ContentControl));
        root.SetBinding(ContentControl.ContentProperty, new Binding { Converter = new IconRowConverter(controller) });
        list.ItemTemplate = new DataTemplate { VisualTree = root }; return list;
    }
}

