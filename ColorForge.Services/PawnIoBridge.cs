using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ColorForge.Services;

public sealed class PawnIoBridge : IDisposable
{
    private const uint DeviceType = 41394;
    private const uint LoadBinaryIoctl = (DeviceType << 16) | (0x821u << 2);
    private const uint ExecuteFunctionIoctl = (DeviceType << 16) | (0x841u << 2);
    private static readonly string[] DevicePaths =
    {
        @"\\?\GLOBALROOT\Device\PawnIO",
        @"\\.\PawnIO"
    };

    private readonly SafeFileHandle _handle;

    private PawnIoBridge(SafeFileHandle handle) => _handle = handle;

    public static bool IsInstalled
    {
        get
        {
            try
            {
                using PawnIoBridge bridge = Open();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public static PawnIoBridge Open()
    {
        int lastError = 2;
        foreach (string path in DevicePaths)
        {
            SafeFileHandle handle = CreateFile(
                path,
                0xC0000000,
                0x00000007,
                IntPtr.Zero,
                3,
                0,
                IntPtr.Zero);
            if (!handle.IsInvalid)
                return new PawnIoBridge(handle);
            lastError = Marshal.GetLastWin32Error();
            handle.Dispose();
        }
        throw new Win32Exception(lastError, "Драйвер PawnIO не установлен или заблокирован Windows.");
    }

    public void LoadModule(byte[] module)
    {
        if (module.Length == 0)
            throw new ArgumentException("Модуль PawnIO пуст.", nameof(module));
        if (!DeviceIoControl(_handle, LoadBinaryIoctl, module, module.Length, null, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "PawnIO отклонил подписанный модуль.");
    }

    public ulong[] Execute(string function, ulong[]? input, int outputCount)
    {
        if (string.IsNullOrWhiteSpace(function) || Encoding.ASCII.GetByteCount(function) >= 32)
            throw new ArgumentException("Некорректное имя функции PawnIO.", nameof(function));
        input ??= Array.Empty<ulong>();
        outputCount = Math.Clamp(outputCount, 0, 256);

        byte[] request = new byte[32 + input.Length * sizeof(ulong)];
        byte[] name = Encoding.ASCII.GetBytes(function);
        Buffer.BlockCopy(name, 0, request, 0, name.Length);
        if (input.Length > 0)
            Buffer.BlockCopy(input, 0, request, 32, input.Length * sizeof(ulong));

        byte[] response = new byte[outputCount * sizeof(ulong)];
        if (!DeviceIoControl(_handle, ExecuteFunctionIoctl, request, request.Length, response, response.Length, out int written, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"PawnIO: функция {function} завершилась ошибкой.");
        if (written % sizeof(ulong) != 0)
            throw new InvalidDataException("PawnIO вернул повреждённый ответ.");

        ulong[] values = new ulong[written / sizeof(ulong)];
        if (written > 0)
            Buffer.BlockCopy(response, 0, values, 0, written);
        return values;
    }

    public static byte[] ReadEmbeddedModule(string fileName)
    {
        var assembly = typeof(PawnIoBridge).Assembly;
        string resource = assembly.GetManifestResourceNames()
            .SingleOrDefault(x => x.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"В AURA отсутствует модуль {fileName}.");
        using Stream stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Не удалось открыть модуль {fileName}.");
        using MemoryStream buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    public void Dispose() => _handle.Dispose();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        byte[]? input,
        int inputSize,
        byte[]? output,
        int outputSize,
        out int bytesReturned,
        IntPtr overlapped);
}
