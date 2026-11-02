using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Markup;

namespace KansoDesktop;

internal static class Ui
{
    internal static SolidColorBrush Brush(string hex, double opacity = 1)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Opacity = opacity; return b;
    }
    internal static TextBlock Text(string text, double size = 14, string color = "#F2EDF5") => new()
    {
        Text = text,
        FontSize = size,
        Foreground = Brush(color),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 12),
        FontFamily = new FontFamily("Segoe UI")
    };
    internal static Button Button(string text, Action action, bool accent = false)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
            Padding = new Thickness(16, 10, 16, 10),
            Margin = new Thickness(0, 0, 8, 8),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = Brush(accent ? "#DFA0B6" : "#292633"),
            Foreground = Brush(accent ? "#211822" : "#F2EDF5"),
            BorderThickness = new Thickness(0),
            FontSize = 13,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        button.Template = (ControlTemplate)XamlReader.Parse("<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'><Border Background='{TemplateBinding Background}' CornerRadius='9' Padding='{TemplateBinding Padding}'><ContentPresenter HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}' VerticalAlignment='Center'/></Border><ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter Property='Opacity' Value='0.8'/></Trigger><Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.4'/></Trigger></ControlTemplate.Triggers></ControlTemplate>");
        button.Click += (_, _) => action(); return button;
    }
    internal static Border Card(UIElement content) => new()
    {
        Child = content,
        Padding = new Thickness(22),
        Background = Brush("#211E2B"),
        CornerRadius = new CornerRadius(14),
        Margin = new Thickness(0, 0, 0, 16)
    };
    internal static StackPanel Stack(params UIElement[] children)
    {
        var p = new StackPanel(); foreach (var c in children) p.Children.Add(c); return p;
    }
    internal static Window Dialog(string title, UIElement content, double width = 500, double height = 560) => new()
    {
        Title = title,
        Width = width,
        Height = height,
        MinWidth = 360,
        MinHeight = 300,
        Background = Brush("#16131E"),
        Foreground = Brush("#F2EDF5"),
        FontFamily = new FontFamily("Segoe UI"),
        WindowStartupLocation = WindowStartupLocation.CenterScreen,
        Content = new ScrollViewer { Content = new Border { Padding = new Thickness(28), Child = content }, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
    };
}

