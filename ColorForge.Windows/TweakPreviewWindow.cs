using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ColorForge.Services;

namespace ColorForge.Windows;

public sealed class TweakPreviewWindow : Window
{
    public TweakPreviewWindow(IReadOnlyList<WindowsTweakDefinition> selected)
    {
        Title = "Проверка изменений Windows"; Width = 820; Height = 600; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(20, 15, 27)); Foreground = Brushes.White;
        DockPanel layout = new() { Margin = new Thickness(18) };
        TextBlock warning = new() { Text = "Исходные параметры сохраняются перед каждым изменением. Danger может отключить защиту или устройства. Точка восстановления создаётся, если Windows позволяет.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(warning, Dock.Top); layout.Children.Add(warning);
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        Button apply = new() { Content = "Применить выбранные изменения", Height = 38, Padding = new Thickness(12, 4, 12, 4) };
        apply.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(apply); buttons.Children.Add(new Button { Content = "Отмена", IsCancel = true, Height = 38, Margin = new Thickness(8, 0, 0, 0) });
        DockPanel.SetDock(buttons, Dock.Bottom); layout.Children.Add(buttons);
        layout.Children.Add(new DataGrid { IsReadOnly = true, AutoGenerateColumns = true, CanUserAddRows = false,
            ItemsSource = selected.Select(x => new { Изменение = x.Title, Категория = x.Category, Риск = x.Risk.ToString(), Совместимость = x.RequiresWindows11 ? "Windows 11" : "Windows 10/11" }).ToArray() });
        Content = layout;
    }
}
