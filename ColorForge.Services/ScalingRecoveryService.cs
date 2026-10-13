using System;
using System.IO;
using System.Text.Json;
using ColorForge.Models;

namespace ColorForge.Services;
public sealed class ScalingRecoveryService
{
    private sealed record Baseline(string MonitorId, uint Scaling);
    public bool Apply(string device, bool stretch, out string message)
    {
        string id = ColorEngine.GetDisplayIdentity(device);
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AURA", "ScalingRecovery", AuthProtocol.Hash(id) + ".dat");
        Baseline? baseline = File.Exists(path) ? JsonSerializer.Deserialize<Baseline>(LicenseStore.UnprotectBytes(File.ReadAllBytes(path))) : null;
        if (baseline != null && baseline.MonitorId != id) throw new InvalidDataException("Снимок другого монитора.");
        if (stretch)
        {
            if (baseline == null) {
                if (!DisplayScalingService.TryReadScaling(device, out uint original)) { message = "Исходное масштабирование не прочитано; изменение пропущено."; return false; }
                baseline = new(id, original);
                AtomicFile.Write(path, LicenseStore.ProtectBytes(JsonSerializer.SerializeToUtf8Bytes(baseline)));
            }
            return DisplayScalingService.TryApply(device, true, out message);
        }
        if (baseline == null) { message = "Сохранённого изменения AURA нет; масштабирование не менялось."; return true; }
        if (!DisplayScalingService.TryApplyScaling(device, baseline.Scaling, out message)) return false;
        File.Delete(path); message = "Исходное масштабирование восстановлено."; return true;
    }
}
