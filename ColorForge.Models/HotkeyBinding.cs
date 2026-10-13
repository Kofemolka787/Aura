using System;
using System.Collections.Generic;
using System.Linq;

namespace ColorForge.Models;

public enum HotkeyAction { ToggleCrosshair, ToggleOverlay, ToggleBenchmark, NextProfile, RestoreColors }
public sealed class HotkeyBinding
{
    public HotkeyAction Action { get; set; }
    public bool Enabled { get; set; } = true;
    public bool Control { get; set; } = true;
    public bool Shift { get; set; } = true;
    public bool Alt { get; set; }
    public string Key { get; set; } = "F6";
    public string Title => Action switch
    {
        HotkeyAction.ToggleCrosshair => "Включить / выключить прицел",
        HotkeyAction.ToggleOverlay => "Включить / выключить оверлей",
        HotkeyAction.ToggleBenchmark => "Начать / отменить замер",
        HotkeyAction.NextProfile => "Следующий цветовой профиль",
        _ => "Вернуть исходные цвета"
    };
    public uint Modifiers => (Control ? 2u : 0) | (Shift ? 4u : 0) | (Alt ? 1u : 0);
    public string Gesture => string.Join("+", new[] { Control ? "Ctrl" : null, Shift ? "Shift" : null, Alt ? "Alt" : null, Key }.Where(x => x != null));
    public HotkeyBinding Clone() => (HotkeyBinding)MemberwiseClone();
    // Windows reserves F12 for the debugger; it is deliberately absent.
    public static IReadOnlyList<string> Keys { get; } = Enumerable.Range(1, 11).Select(x => "F" + x)
        .Concat(Enumerable.Range('A', 26).Select(x => ((char)x).ToString()))
        .Concat(Enumerable.Range(0, 10).Select(x => x.ToString())).ToArray();
    public static uint VirtualKey(string key)
    {
        if (!Keys.Contains(key, StringComparer.Ordinal)) return 0;
        return key.Length == 1 ? key[0] : (uint)(0x6F + int.Parse(key[1..]));
    }
    public static List<HotkeyBinding> Defaults() => Enum.GetValues<HotkeyAction>()
        .Select((action, index) => new HotkeyBinding { Action = action, Key = "F" + (index + 6) }).ToList();
}
