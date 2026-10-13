using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ColorForge.Models;

namespace ColorForge.Windows;

public sealed class HotkeysWindow : SettingsDialog
{
    public List<HotkeyBinding> Result { get; }
    public HotkeysWindow(IEnumerable<HotkeyBinding> settings) : base("AURA · Горячие клавиши", 830, 450)
    {
        Result = settings.Select(x => x.Clone()).ToList();
        foreach (HotkeyBinding missing in HotkeyBinding.Defaults())
            if (!Result.Any(x => x.Action == missing.Action)) { missing.Enabled = false; Result.Add(missing); }
        Form.Children.Add(Label("Сочетания работают и при свёрнутой AURA. Нужен хотя бы один модификатор. Занятые другой программой сочетания будут показаны после сохранения."));
        DataGrid grid = new() { ItemsSource = Result, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, Height = 230 };
        grid.Columns.Add(new DataGridCheckBoxColumn { Header = "Вкл", Binding = new Binding("Enabled") });
        grid.Columns.Add(new DataGridTextColumn { Header = "Действие", Binding = new Binding("Title"), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        foreach (string field in new[] { "Control", "Shift", "Alt" })
            grid.Columns.Add(new DataGridCheckBoxColumn { Header = field == "Control" ? "Ctrl" : field, Binding = new Binding(field) });
        grid.Columns.Add(new DataGridComboBoxColumn { Header = "Клавиша", ItemsSource = HotkeyBinding.Keys, SelectedItemBinding = new Binding("Key"), Width = 100 });
        Form.Children.Add(grid);
        Form.Children.Add(Label("Замер: начинает выбранный процесс; повторное нажатие отменяет текущий прогон. Для серии перед каждым прогоном вернись к началу сцены."));
        Buttons(() =>
        {
            grid.CommitEdit(DataGridEditingUnit.Cell, true); grid.CommitEdit(DataGridEditingUnit.Row, true);
            if (Result.Any(x => x.Enabled && (x.Modifiers == 0 || HotkeyBinding.VirtualKey(x.Key) == 0)))
                throw new InvalidOperationException("Выбери клавишу и Ctrl, Shift или Alt для каждого включённого действия.");
            if (Result.Where(x => x.Enabled).GroupBy(x => x.Gesture).Any(g => g.Count() > 1))
                throw new InvalidOperationException("Одно сочетание назначено нескольким действиям.");
        });
    }
}
