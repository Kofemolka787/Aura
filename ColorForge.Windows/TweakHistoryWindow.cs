using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ColorForge.Models;

namespace ColorForge.Windows;

public sealed class TweakHistoryWindow : Window
{
    public TweakHistoryWindow(IReadOnlyList<TweakRunReport> reports)
    {
        Title = "История изменений AURA"; Width = 900; Height = 580; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new DataGrid { IsReadOnly = true, AutoGenerateColumns = true, CanUserAddRows = false,
            ItemsSource = reports.SelectMany(x => x.Actions).OrderByDescending(x => x.TimeUtc)
                .Select(x => new { Время = x.TimeUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm"), Настройка = x.Title,
                    Состояние = x.Status switch { "Applied" => "Применена", "Restored" => "Восстановлена", "Skipped" => "Пропущена", "Failed" => "Ошибка", _ => "Прервана/ожидает" }, Подробности = x.Detail }).ToArray() };
    }
}
