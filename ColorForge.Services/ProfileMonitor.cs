using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using ColorForge.Models;

namespace ColorForge.Services;

public sealed class ProfileMonitor : IDisposable
{
	private readonly Timer _timer;

	private readonly Func<IReadOnlyList<GameBinding>> _bindings;

	private string? _activeProcess;

	public event Action<GameBinding?>? ActiveGameChanged;

	public ProfileMonitor(Func<IReadOnlyList<GameBinding>> bindings)
	{
		_bindings = bindings;
		_timer = new Timer(Check, null, -1, -1);
	}

	public void Start()
	{
		_timer.Change(0, 1000);
	}

	public void Stop()
	{
		_timer.Change(-1, -1);
		_activeProcess = null;
	}

	private void Check(object? state)
	{
		try
		{
			GameBinding gameBinding = null;
			foreach (GameBinding item in from x in _bindings()
				where x.Enabled
				select x)
			{
				if (Process.GetProcessesByName(Path.GetFileNameWithoutExtension(item.ProcessName)).Length != 0)
				{
					gameBinding = item;
					break;
				}
			}
			string text = gameBinding?.ProcessName;
			if (!string.Equals(text, _activeProcess, StringComparison.OrdinalIgnoreCase))
			{
				_activeProcess = text;
				this.ActiveGameChanged?.Invoke(gameBinding);
			}
		}
		catch
		{
		}
	}

	public void Dispose()
	{
		_timer.Dispose();
	}
}
