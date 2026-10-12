using System.Collections.Generic;

namespace ColorForge.Models;

public sealed class WindowsTweakPreferences
{
	public int CatalogVersion { get; set; }

	public List<string> EnabledTweakIds { get; set; } = new List<string>();

	// Legacy fields are retained so existing settings.json files migrate cleanly.
	public bool FastStartup { get; set; } = true;

	public bool RemoveStartupDelay { get; set; } = true;

	public bool GamingPerformance { get; set; } = true;

	public bool ReduceVisualEffects { get; set; } = true;

	public bool DisableBackgroundApps { get; set; } = true;

	public bool ReduceTelemetry { get; set; } = true;

	public bool RemoveCopilot { get; set; } = true;

	public bool RemoveConsumerApps { get; set; } = true;

	public bool RemoveXboxApps { get; set; } = true;

	public bool DisableSysMain { get; set; }

	public bool DisableWindowsSearch { get; set; }

	public bool DisablePrintSpooler { get; set; }

	public bool RemoveMicrosoftStore { get; set; }
}
