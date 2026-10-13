using System;
using System.Threading;
using System.Threading.Tasks;
using ColorForge.Models;

namespace ColorForge.Services;

public interface IHardwareTelemetry
{
    HardwareSnapshot ReadSnapshot(string? preferredGpuName = null);
}

public static class HardwareSafety
{
    public static double RequireTemperature(HardwareSnapshot snapshot, bool gpu)
    {
        double? value = gpu ? snapshot.GpuTemperatureC : snapshot.CpuTemperatureC;
        if (!snapshot.TelemetryValid || !value.HasValue || !double.IsFinite(value.Value)
            || value < 0 || value > 130 || DateTime.UtcNow - snapshot.TimestampUtc > TimeSpan.FromSeconds(3)
            || snapshot.TimestampUtc > DateTime.UtcNow.AddSeconds(1))
            throw new InvalidOperationException("Температурный контроль потерян. Нагрузка остановлена; выполняется возврат исходных параметров.");
        return value.Value;
    }
}

// One gate for all writes, including reset and startup application.
public static class HardwareOperations
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public static async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease();
    }
    public static IDisposable Enter()
    {
        if (!Gate.Wait(0))
            throw new InvalidOperationException("Сначала останови текущую аппаратную операцию.");
        return new Lease();
    }
    private sealed class Lease : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Gate.Release();
        }
    }
}

public static class IntelPowerPolicy
{
    public static (double Pl1, double Pl2) Decode(ulong raw, double unit) =>
        ((raw & 0x7FFF) * unit, ((raw >> 32) & 0x7FFF) * unit);

    public static ulong WithLimits(ulong original, double unit, double pl1, double pl2, double maximum)
    {
        if ((original & (1UL << 63)) != 0)
            throw new NotSupportedException("Лимиты Intel заблокированы BIOS.");
        if (!double.IsFinite(unit) || unit <= 0 || !double.IsFinite(maximum) || maximum <= 0
            || !double.IsFinite(pl1) || !double.IsFinite(pl2) || pl1 <= 0 || pl2 < pl1 || pl2 > maximum)
            throw new ArgumentOutOfRangeException(nameof(pl1), "Лимиты отсутствуют или превышают подтверждённый диапазон CPU.");
        ulong a = checked((ulong)Math.Round(pl1 / unit));
        ulong b = checked((ulong)Math.Round(pl2 / unit));
        if (a is 0 or > 0x7FFF || b is 0 or > 0x7FFF)
            throw new ArgumentOutOfRangeException(nameof(pl1));
        return (original & ~(0x7FFFUL | (0x7FFFUL << 32))) | a | (b << 32);
    }
}
