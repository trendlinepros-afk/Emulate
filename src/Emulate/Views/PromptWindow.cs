using System.Windows;
using System.Windows.Controls;
using Emulate.Services;

namespace Emulate.Views;

/// <summary>Small "enter a name" dialog.</summary>
public sealed class PromptWindow : Window
{
    private readonly TextBox _box;

    private PromptWindow(string title, string message, string initial)
    {
        Title = title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Style = (Style)Application.Current.Resources["DarkWindow"];
        SourceInitialized += (_, _) => Native.ApplyDarkTitleBar(this);

        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["H1"], Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(new TextBlock { Text = message, Style = (Style)Application.Current.Resources["MutedText"], Margin = new Thickness(0, 0, 0, 12) });
        _box = new TextBox { Text = initial };
        panel.Children.Add(_box);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true });
        var ok = new Button { Content = "OK", IsDefault = true, Style = (Style)Application.Current.Resources["Primary"], Margin = new Thickness(0) };
        ok.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }

    public static string? Ask(Window owner, string title, string message, string initial)
    {
        var w = new PromptWindow(title, message, initial) { Owner = owner };
        return w.ShowDialog() == true ? w._box.Text : null;
    }
}
