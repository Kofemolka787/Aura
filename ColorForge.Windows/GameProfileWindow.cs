using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ColorForge.Models;
using ColorForge.Services;

namespace ColorForge.Windows;

public sealed class GameProfileWindow : SettingsDialog
{
    private sealed record MonitorChoice(string Device, string Title) { public override string ToString() => Title; }
    public GameBinding Result { get; }
    public GameProfileWindow(GameBinding game, AppSettings settings, IReadOnlyList<MonitorTarget> monitors)
        : base("AURA · Карточка игры", 590, 700)
    {
        Result = game.Clone();
        TextBox name = Input("Название игры", game.Title, 64);
        TextBox process = Input("Имя процесса игры (без пути)", game.ProcessName, 128);
        CheckBox enabled = Check("Автоматически применять выбранные настройки", game.Enabled);
        List<MonitorChoice> choices = new() { new("", "Выбранный в AURA монитор") };
        choices.AddRange(monitors.Select(x => new MonitorChoice(x.DeviceName, x.Label)));
        if (!string.IsNullOrEmpty(game.MonitorDeviceName) && choices.All(x => x.Device != game.MonitorDeviceName))
            choices.Add(new(game.MonitorDeviceName, game.MonitorDeviceName + " · сейчас отключён"));
        ComboBox monitor = Select("Монитор", choices, choices.FirstOrDefault(x => x.Device == game.MonitorDeviceName) ?? choices[0]);
        CheckBox color = Check("Применять цветовой профиль", game.ApplyColor);
        ComboBox profile = Select("Цветовой профиль", settings.Profiles.Select(x => x.Name).ToArray(), game.ProfileName);
        CheckBox restore = Check("Вернуть общие настройки при Alt-Tab (иначе — при выходе из игры)", game.RestoreOnFocusLoss);
        CheckBox useCrosshair = Check("Использовать отдельный прицел для этой игры", game.UseCrosshair);
        CrosshairSettings initial = game.UseCrosshair ? game.Crosshair : settings.Crosshair;
        CheckBox crosshair = Check("Показывать прицел в этой игре", initial.Enabled);
        ComboBox shape = Select("Форма", Enum.GetValues<CrosshairShape>(), initial.Shape);
        TextBox size = Input("Размер 1–40", initial.Size.ToString(), 2);
        TextBox gap = Input("Зазор 0–40", initial.Gap.ToString(), 2);
        TextBox thickness = Input("Толщина 1–10", initial.Thickness.ToString(), 2);
        TextBox outline = Input("Обводка 0–10", initial.Outline.ToString(), 2);
        TextBox argb = Input("Цвет ARGB · например FF00FF00", unchecked((uint)initial.Argb).ToString("X8"), 8);
        CheckBox useOverlay = Check("Использовать отдельный оверлей для этой игры", game.UseOverlay);
        if (!game.UseOverlay) Result.Overlay = settings.Overlay.Clone();
        Button overlay = new() { Content = "Настроить игровой оверлей", Style = (Style)FindResource("SecondaryButton") };
        overlay.Click += (_, _) => { OverlaySettingsWindow dialog = new(Result.Overlay) { Owner = this }; if (dialog.ShowDialog() == true) Result.Overlay = dialog.Result; };
        Form.Children.Add(overlay);
        Form.Children.Add(Label("Разрешение меняется на странице «Монитор» с подтверждением за 15 секунд. Карточка автоматически применяет выбранные цвет, прицел и оверлей."));
        Buttons(() =>
        {
            string executable = process.Text.Trim();
            if (executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) executable = executable[..^4];
            if (executable.Length == 0 || executable is "." or ".." || executable.IndexOfAny(new[] { '\\', '/', '"', '\r', '\n', ':', '*', '?' }) >= 0)
                throw new InvalidOperationException("Укажи имя процесса, например RustClient или cs2.");
            if (settings.Games.Any(x => !ReferenceEquals(x, game) && x.ProcessName.Equals(executable, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Для этого процесса уже есть карточка.");
            if (profile.SelectedItem is not string profileName) throw new InvalidOperationException("Выбери цветовой профиль.");
            if (argb.Text.Length != 8 || !uint.TryParse(argb.Text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
                throw new InvalidOperationException("Цвет должен быть в формате ARGB из восьми шестнадцатеричных цифр.");
            int Number(TextBox box, int min, int max) => int.TryParse(box.Text, out int value) && value >= min && value <= max
                ? value : throw new InvalidOperationException($"Проверь параметры прицела: допустимо {min}–{max}.");
            Result.DisplayName = name.Text.Trim(); Result.ProcessName = executable; Result.ProfileName = profileName;
            Result.Enabled = enabled.IsChecked == true; Result.ApplyColor = color.IsChecked == true;
            Result.MonitorDeviceName = ((MonitorChoice)monitor.SelectedItem).Device; Result.RestoreOnFocusLoss = restore.IsChecked == true;
            Result.UseCrosshair = useCrosshair.IsChecked == true; Result.UseOverlay = useOverlay.IsChecked == true;
            Result.Crosshair = new() { Enabled = crosshair.IsChecked == true, Shape = (CrosshairShape)shape.SelectedItem,
                Size = Number(size, 1, 40), Gap = Number(gap, 0, 40), Thickness = Number(thickness, 1, 10), Outline = Number(outline, 0, 10), Argb = unchecked((int)rgb) };
        });
    }
}
