using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ColorForge.Services;

namespace ColorForge.Windows;

public sealed class DisplayModeConfirmationWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly TextBlock _countdown = new();
    public DisplayModeConfirmationWindow(DisplayMode mode)
    {
        Title = "Сохранить новый режим?"; Width = 460; Height = 220; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(20, 15, 27)); Foreground = Brushes.White;
        StackPanel body = new() { Margin = new Thickness(22) };
        body.Children.Add(new TextBlock { Text = mode.ToString(), FontSize = 20, FontWeight = FontWeights.Bold, Foreground = Brushes.White });
        _countdown.Foreground = Brushes.White; _countdown.Margin = new Thickness(0, 12, 0, 12); body.Children.Add(_countdown);
        StackPanel buttons = new() { Orientation = Orientation.Horizontal };
        Button keep = new() { Content = "Сохранить", Width = 150, Height = 38, IsDefault = true };
        keep.Click += (_, _) => DialogResult = true;
        Button revert = new() { Content = "Вернуть прежний", Width = 180, Height = 38, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        buttons.Children.Add(keep); buttons.Children.Add(revert); body.Children.Add(buttons); Content = body;
        int seconds = 15;
        _countdown.Text = "Автоматический возврат через 15 секунд";
        _timer.Tick += (_, _) => { seconds--; _countdown.Text = $"Автоматический возврат через {seconds} секунд"; if (seconds <= 0) DialogResult = false; };
        Loaded += (_, _) => _timer.Start(); Closed += (_, _) => _timer.Stop();
    }
}
