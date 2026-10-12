using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ColorForge.Services;

namespace ColorForge.Models;

public sealed class TweakOptionViewModel : INotifyPropertyChanged
{
    private bool _isSelected;

    public TweakOptionViewModel(WindowsTweakDefinition definition, bool isSelected)
    {
        Definition = definition;
        _isSelected = isSelected;
    }

    public WindowsTweakDefinition Definition { get; }
    public string Id => Definition.Id;
    public string Title => Definition.Title;
    public string Description => Definition.Description;
    public WindowsTweakRisk Risk => Definition.Risk;
    public bool RequiresWindows11 => Definition.RequiresWindows11;
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
