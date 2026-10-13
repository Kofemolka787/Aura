using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ColorForge.Services;

namespace ColorForge.Models;

public sealed class TweakOptionViewModel : INotifyPropertyChanged
{
    private bool _isSelected;
    private string _statusText = "Нет сохранённого отчёта";
    public string StatusText => _statusText;
    public void UpdateStatus(string status)
    {
        _statusText = status switch { "Applied" => "Применён", "Restored" => "Восстановлен", "Skipped" => "Пропущен", "Failed" => "Ошибка", _ => "Не подтверждено" };
        OnPropertyChanged(nameof(StatusText));
    }

    public TweakOptionViewModel(WindowsTweakDefinition definition, bool isSelected)
    {
        Definition = definition;
        _isSelected = isSelected && IsAvailable;
    }

    public WindowsTweakDefinition Definition { get; }
    public string Id => Definition.Id;
    public string Title => Definition.Title;
    public string Description => Definition.Description;
    public WindowsTweakRisk Risk => Definition.Risk;
    public bool RequiresWindows11 => Definition.RequiresWindows11;
    public bool IsAvailable => WindowsTweaksService.HasVerifiedRestore(Id);
    public string AvailabilityText => IsAvailable ? string.Empty : Id == "disable_hibernation" ? "Недоступно: нужно точное восстановление типа и размера hiberfil.sys" : "Удаление недоступно: нужен источник MSIX для восстановления";
    public string CompatibilityText => RequiresWindows11 ? "WINDOWS 11" : string.Empty;
    public string RiskText => Risk switch
    {
        WindowsTweakRisk.Safe => "SAFE",
        WindowsTweakRisk.Caution => "CAUTION",
        _ => "DANGER"
    };
    public string RiskForeground => Risk switch
    {
        WindowsTweakRisk.Safe => "#63E2A0",
        WindowsTweakRisk.Caution => "#FFC35C",
        _ => "#FF7181"
    };
    public string RiskBackground => Risk switch
    {
        WindowsTweakRisk.Safe => "#13271F",
        WindowsTweakRisk.Caution => "#302313",
        _ => "#32171D"
    };

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            value = value && IsAvailable;
            if (_isSelected == value)
                return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class TweakGroupViewModel
{
    public TweakGroupViewModel(string title, IReadOnlyList<TweakOptionViewModel> items)
    {
        Title = title;
        Items = items;
    }

    public string Title { get; }
    public IReadOnlyList<TweakOptionViewModel> Items { get; }
    public string CountText => $"{Items.Count} ПУНКТОВ";
}
