using System;
using System.Collections.Generic;

namespace ColorForge.Models;

public sealed class TweakActionStatus
{
    public string RunId { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public DateTime TimeUtc { get; set; }
}
public sealed class TweakRunReport
{
    public string RunId { get; set; } = string.Empty;
    public DateTime RecordedAtUtc { get; set; }
    public bool Success { get; set; }
    public List<TweakActionStatus> Actions { get; set; } = new();
    public List<string> Errors { get; set; } = new();
    public string RestorePoint { get; set; } = string.Empty;
    public string LogPath { get; set; } = string.Empty;
    public int Remaining { get; set; }
}
