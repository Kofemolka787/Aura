using System;
using System.Windows.Controls;
using ColorForge.Models;

namespace ColorForge.Windows;

public sealed class OverlaySettingsWindow : SettingsDialog
{
    public OverlaySettings Result { get; }
    public OverlaySettingsWindow(OverlaySettings value) : base("AURA · Игровой оверлей", 540, 620)
    {
        Result = value.Clone();
        Form.Children.Add(Label("Внешнее окно без внедрения в игру. Начни с оконного или безрамочного режима. Совместимость зависит от игры и её правил."));
        CheckBox enabled = Check("Включить оверлей", value.Enabled);
        CheckBox fps = Check("FPS и 1% low · целевое окно примерно 2 секунды", value.ShowFps);
        CheckBox graph = Check("График времени кадров", value.ShowFrameGraph);
        CheckBox cpu = Check("Температура и нагрузка CPU", value.ShowCpu);
        CheckBox gpu = Check("Температура, нагрузка и частота GPU", value.ShowGpu);
        CheckBox vram = Check("Использование VRAM", value.ShowVram);
        CheckBox profile = Check("Название активного профиля", value.ShowProfile);
        string[] corners = { "Слева сверху", "Справа сверху", "Слева снизу", "Справа снизу" };
        ComboBox corner = Select("Положение", corners, corners[(int)value.Corner]);
        Form.Children.Add(Label("Размер")); Slider scale = new() { Minimum = 0.7, Maximum = 2, Value = value.Scale }; Form.Children.Add(scale);
        Form.Children.Add(Label("Непрозрачность")); Slider opacity = new() { Minimum = 0.25, Maximum = 1, Value = value.Opacity }; Form.Children.Add(opacity);
        Buttons(() =>
        {
            Result.Enabled = enabled.IsChecked == true; Result.ShowFps = fps.IsChecked == true;
            Result.ShowFrameGraph = graph.IsChecked == true; Result.ShowCpu = cpu.IsChecked == true;
            Result.ShowGpu = gpu.IsChecked == true; Result.ShowVram = vram.IsChecked == true; Result.ShowProfile = profile.IsChecked == true;
            Result.Corner = (OverlayCorner)corner.SelectedIndex; Result.Scale = scale.Value; Result.Opacity = opacity.Value;
        });
    }
}
