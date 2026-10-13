using System;
using Microsoft.Win32;

namespace ColorForge.Services;

public sealed record CpuPowerState(string ProcessorName, string DeviceId, ulong RawLimit, double PowerUnit, double MaximumWatts, double TemperatureLimitC)
{
    public double Pl1 => IntelPowerPolicy.Decode(RawLimit, PowerUnit).Pl1;
    public double Pl2 => IntelPowerPolicy.Decode(RawLimit, PowerUnit).Pl2;
}

public interface ICpuPowerController
{
    string ProcessorName { get; }
    CpuPowerState Read();
    void WriteVerified(ulong value);
}

internal sealed class IntelPowerController : ICpuPowerController
{
    public string ProcessorName
    {
        get
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return key?.GetValue("ProcessorNameString")?.ToString()?.Trim() ?? "Процессор не определён";
        }
    }
    private static PawnIoBridge Open()
    {
        byte[] module = PawnIoBridge.ReadEmbeddedModule("IntelMSR.bin");
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(module)).ToLowerInvariant();
        if (hash != "d6ed85d65ab17a22f813ef98207d6d537155ee2ded5976a21cb48413c9b92e5f")
            throw new InvalidOperationException("Контрольная сумма Intel-модуля не совпадает.");
        PawnIoBridge bridge = PawnIoBridge.Open();
        try { bridge.LoadModule(module); return bridge; }
        catch { bridge.Dispose(); throw; }
    }
    private static ulong ReadMsr(PawnIoBridge bridge, ulong address)
    {
        ulong[] values = bridge.Execute("ioctl_read_msr", new[] { address }, 1);
        if (values.Length != 1) throw new InvalidOperationException("MSR не вернул полное значение.");
        return values[0];
    }
    public CpuPowerState Read()
    {
        string name = ProcessorName;
        if (!name.Contains("Intel", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Автонастройка AMD отключена: текущий адаптер не умеет достоверно читать и возвращать исходные PBO-лимиты. Настройки BIOS не изменяются.");
        using PawnIoBridge bridge = Open();
        double unit = 1d / (1 << (int)(ReadMsr(bridge, 0x606) & 0xF));
        ulong raw = ReadMsr(bridge, 0x610);
        double maximum = ((ReadMsr(bridge, 0x614) >> 32) & 0x7FFF) * unit;
        double tjMax = (ReadMsr(bridge, 0x1A2) >> 16) & 0xFF;
        if ((raw & (1UL << 63)) != 0 || maximum <= 0 || tjMax < 70 || tjMax > 120)
            throw new NotSupportedException("BIOS заблокировал запись или CPU не объявил достоверные пределы мощности/температуры.");
        CpuPowerState state = new(name, DeviceIdentityService.GetDeviceId() + "|" + name, raw, unit, maximum, Math.Min(92, tjMax - 10));
        if (state.Pl1 <= 0 || state.Pl2 < state.Pl1)
            throw new NotSupportedException("Не удалось прочитать действующие PL1/PL2.");
        return state;
    }
    public void WriteVerified(ulong value)
    {
        using PawnIoBridge bridge = Open();
        bridge.Execute("ioctl_write_msr", new[] { 0x610UL, value }, 0);
        if (ReadMsr(bridge, 0x610) != value)
            throw new InvalidOperationException("Intel не подтвердил аппаратную запись. Проверь BIOS и журнал AURA.");
    }
}
