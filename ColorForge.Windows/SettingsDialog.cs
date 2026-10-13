using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ColorForge.Windows;

public class SettingsDialog : Window
{
    protected StackPanel Form { get; } = new() { Margin = new Thickness(22) };
    protected TextBlock Error { get; } = new() { Foreground = Brushes.Salmon, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    public SettingsDialog(string title, double width = 600, double height = 650)
    {
        Title = title; Width = width; Height = height; MinWidth = width; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(20, 15, 27)); Foreground = Brushes.White;
        Content = new ScrollViewer { Content = Form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
    protected TextBox Input(string label, string text, int maximum = 128)
    {
        Form.Children.Add(Label(label));
        TextBox box = new() { Text = text, MaxLength = maximum, Margin = new Thickness(0, 0, 0, 9) };
        Form.Children.Add(box); return box;
    }
    protected CheckBox Check(string label, bool value)
    {
        CheckBox box = new() { Content = label, IsChecked = value, Foreground = Brushes.White, Margin = new Thickness(0, 9, 0, 9) };
        Form.Children.Add(box); return box;
    }
    protected static TextBlock Label(string label) => new() { Text = label, Foreground = Brushes.LightGray,
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 6) };
    protected ComboBox Select(string label, System.Collections.IEnumerable items, object? selected)
    {
        Form.Children.Add(Label(label)); ComboBox box = new() { ItemsSource = items, SelectedItem = selected, Margin = new Thickness(0, 0, 0, 8) };
        Form.Children.Add(box); return box;
    }
    protected void Buttons(Action save)
    {
        Form.Children.Add(Error);
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        Button cancel = new() { Content = "Отмена", IsCancel = true, Style = (Style)FindResource("SecondaryButton"), Margin = new Thickness(0, 0, 8, 0) };
        Button ok = new() { Content = "Сохранить", Style = (Style)FindResource("PrimaryButton") };
        ok.Click += (_, _) => { try { save(); DialogResult = true; } catch (Exception e) { Error.Text = e.Message; } };
        buttons.Children.Add(cancel); buttons.Children.Add(ok); Form.Children.Add(buttons);
    }
}
