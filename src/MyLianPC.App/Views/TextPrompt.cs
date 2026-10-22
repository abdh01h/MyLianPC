using System.Windows;
using System.Windows.Controls;

namespace MyLianPC.App.Views;

/// <summary>Small modal text-input dialog.</summary>
public static class TextPrompt
{
    public static string? Ask(Window owner, string title, string message, string initial)
    {
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 10, 0, 16), MinWidth = 320 };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 });
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        var w = new Window
        {
            Title = title, Owner = owner, Content = panel, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
        };
        ok.Click += (_, _) => w.DialogResult = true;
        w.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return w.ShowDialog() == true && box.Text.Trim() is { Length: > 0 } t ? t : null;
    }
}
