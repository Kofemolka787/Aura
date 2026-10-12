namespace ColorForge.Models;

public enum AuditSeverity
{
    Good,
    Info,
    Caution,
    Critical
}

public sealed class AuditItem
{
    public string Title { get; init; } = string.Empty;

    public string Detail { get; init; } = string.Empty;

    public AuditSeverity Severity { get; init; }

    public string Badge => Severity switch
    {
        AuditSeverity.Good => "ГОТОВО",
        AuditSeverity.Info => "ИНФО",
        AuditSeverity.Caution => "ВНИМАНИЕ",
        _ => "ПРОБЛЕМА"
    };
}
