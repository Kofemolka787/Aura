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
    public int Occurrences { get; init; } = 1;
    public string Source { get; init; } = string.Empty;
    public System.DateTimeOffset? TimestampUtc { get; init; }

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
