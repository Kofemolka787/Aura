using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ColorForge.Models;

namespace ColorForge.Services;

public sealed class LicenseStore
{
	private const int CryptProtectUiForbidden = 0x1;

	private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AURA-LICENSE-STORE-v1");

	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
	{
		WriteIndented = false,
		PropertyNameCaseInsensitive = true
	};

	public string DirectoryPath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AURA");

	public string SessionPath => Path.Combine(DirectoryPath, "license.dat");

	public AuthSession? Load()
	{
		try
		{
			if (!File.Exists(SessionPath))
			{
				return null;
			}
			byte[] encrypted = File.ReadAllBytes(SessionPath);
			byte[] json = Unprotect(encrypted);
			return JsonSerializer.Deserialize<AuthSession>(json, JsonOptions);
		}
		catch (Exception exception)
		{
			AppLogService.Error("Stored activation session could not be decrypted or parsed; it will be cleared.", exception);
			Clear();
			return null;
		}
	}

	public void Save(AuthSession session)
	{
		Directory.CreateDirectory(DirectoryPath);
		byte[] json = JsonSerializer.SerializeToUtf8Bytes(session, JsonOptions);
		byte[] encrypted = Protect(json);
		AtomicFile.Write(SessionPath, encrypted);
	}

	public void Clear()
	{
		try
		{
			if (File.Exists(SessionPath))
			{
				File.Delete(SessionPath);
			}
		}
		catch
		{
		}
	}

    internal static byte[] ProtectBytes(byte[] data) => Protect(data);
    internal static byte[] UnprotectBytes(byte[] data) => Unprotect(data);

    private static byte[] Protect(byte[] data)
	{
		return RunCrypt(data, protect: true);
	}

	private static byte[] Unprotect(byte[] data)
	{
		return RunCrypt(data, protect: false);
	}

	private static byte[] RunCrypt(byte[] data, bool protect)
	{
		DataBlob input = CreateBlob(data);
		DataBlob entropy = CreateBlob(Entropy);
		DataBlob output = default;
		try
		{
			bool success = protect
				? CryptProtectData(ref input, null, ref entropy, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output)
				: CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output);
			if (!success)
			{
				throw new Win32Exception(Marshal.GetLastWin32Error());
			}
			byte[] result = new byte[output.Size];
			Marshal.Copy(output.Data, result, 0, output.Size);
			return result;
		}
		finally
		{
			if (input.Data != IntPtr.Zero)
			{
				Marshal.FreeHGlobal(input.Data);
			}
			if (entropy.Data != IntPtr.Zero)
			{
				Marshal.FreeHGlobal(entropy.Data);
			}
			if (output.Data != IntPtr.Zero)
			{
				LocalFree(output.Data);
			}
		}
	}

	private static DataBlob CreateBlob(byte[] data)
	{
		DataBlob blob = new DataBlob { Size = data.Length };
		blob.Data = Marshal.AllocHGlobal(data.Length);
		Marshal.Copy(data, 0, blob.Data, data.Length);
		return blob;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct DataBlob
	{
		public int Size;
		public IntPtr Data;
	}

	[DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob optionalEntropy, IntPtr reserved, IntPtr promptStruct, int flags, out DataBlob dataOut);

	[DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob optionalEntropy, IntPtr reserved, IntPtr promptStruct, int flags, out DataBlob dataOut);

	[DllImport("kernel32.dll")]
	private static extern IntPtr LocalFree(IntPtr memory);
}
