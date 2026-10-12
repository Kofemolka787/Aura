using System;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace ColorForge.Services;

public static class DeviceIdentityService
{
	public static string GetDeviceId()
	{
		string machineGuid = string.Empty;
		try
		{
			using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
			machineGuid = key?.GetValue("MachineGuid")?.ToString() ?? string.Empty;
		}
		catch
		{
		}

		string source = string.IsNullOrWhiteSpace(machineGuid)
			? string.Join("|", Environment.MachineName, Environment.UserName, "AURA-DEVICE-v1")
			: machineGuid + "|AURA-DEVICE-v1";
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
	}

	public static string GetShortDeviceId()
	{
		string id = GetDeviceId();
		return id.Length >= 12 ? id[..12].ToUpperInvariant() : id.ToUpperInvariant();
	}
}
