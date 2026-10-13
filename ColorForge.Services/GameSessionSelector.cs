using System;
using System.Collections.Generic;
using System.Linq;
using ColorForge.Models;

namespace ColorForge.Services;

public static class GameSessionSelector
{
    public static GameBinding? Select(IReadOnlyList<GameBinding> bindings, string foreground,
        Func<string, bool> isRunning, string? previousProcess)
    {
        GameBinding? active = bindings.FirstOrDefault(x => x.Enabled && x.ProcessName.Equals(foreground, StringComparison.OrdinalIgnoreCase));
        if (active != null) return active.Clone();
        GameBinding? previous = bindings.FirstOrDefault(x => x.Enabled && !x.RestoreOnFocusLoss
            && x.ProcessName.Equals(previousProcess, StringComparison.OrdinalIgnoreCase));
        return previous != null && isRunning(previous.ProcessName) ? previous.Clone() : null;
    }
}
