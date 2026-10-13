using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ColorForge.Models;

namespace ColorForge.Services;

internal sealed class ColorBaselineStore
{
    private readonly HardwareRecoveryStore _boot = new();
    private readonly string _root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AURA", "ColorRecovery");
    private string PathFor(string id) => Path.Combine(_root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))) + ".dat");
    public ColorBaseline? Load(string id)
    {
        string path = PathFor(id);
        if (!File.Exists(path)) return null;
        ColorBaseline baseline = JsonSerializer.Deserialize<ColorBaseline>(LicenseStore.UnprotectBytes(File.ReadAllBytes(path)))
            ?? throw new InvalidDataException("Снимок цвета повреждён.");
        if (baseline.DeviceId != id || (baseline.Gamma != null && baseline.Gamma.Length != 768)) throw new InvalidDataException("Некорректный снимок монитора.");
        return baseline.BootId == _boot.BootId ? baseline : null;
    }
    public ColorBaseline Save(ColorBaseline baseline)
    {
        if (baseline.BootId.Length == 0) baseline.BootId = _boot.BootId;
        AtomicFile.Write(PathFor(baseline.DeviceId), LicenseStore.ProtectBytes(JsonSerializer.SerializeToUtf8Bytes(baseline)));
        return baseline;
    }
}
