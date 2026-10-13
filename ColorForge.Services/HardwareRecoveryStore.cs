using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ColorForge.Models;

namespace ColorForge.Services;

public interface IHardwareRecoveryStore
{
    string BootId { get; }
    bool HasSnapshots(string kind) => false;
    IReadOnlyList<HardwareChangeSnapshot> PendingSnapshots(string kind) => Array.Empty<HardwareChangeSnapshot>();
    HardwareChangeSnapshot? Load(string kind, string deviceId);
    void Save(HardwareChangeSnapshot snapshot);
}

public sealed class HardwareRecoveryStore : IHardwareRecoveryStore
{
    private readonly string _root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AURA", "Recovery");
    public string BootId
    {
        get
        {
            int status = NtQuerySystemInformation(90, out BootEnvironment info, Marshal.SizeOf<BootEnvironment>(), out _);
            if (status != 0 || info.BootIdentifier == Guid.Empty)
                throw new NotSupportedException("Windows не предоставила идентификатор загрузки. Аппаратная запись заблокирована.");
            return info.BootIdentifier.ToString("N");
        }
    }

    private string PathFor(string kind, string id) =>
        Path.Combine(_root, kind + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(kind + "|" + id))) + ".dat");

    public bool HasSnapshots(string kind) => Directory.Exists(_root) && Directory.GetFiles(_root, kind + "-*.dat").Length > 0;

    public IReadOnlyList<HardwareChangeSnapshot> PendingSnapshots(string kind)
    {
        if (!Directory.Exists(_root)) return Array.Empty<HardwareChangeSnapshot>();
        string[] files = Directory.GetFiles(_root, kind + "-*.dat");
        if (files.Length == 0) return Array.Empty<HardwareChangeSnapshot>();
        if (files.Length > 128) throw new InvalidDataException("Слишком много аппаратных снимков.");
        string boot = BootId;
        List<HardwareChangeSnapshot> pending = new();
        foreach (string path in files)
        {
            if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Аппаратный снимок слишком большой.");
            var snapshot = JsonSerializer.Deserialize<HardwareChangeSnapshot>(LicenseStore.UnprotectBytes(File.ReadAllBytes(path)))
                ?? throw new InvalidDataException("Аппаратный снимок повреждён.");
            if (snapshot.Kind != kind || PathFor(kind, snapshot.DeviceId) != path) throw new InvalidDataException("Неверное устройство снимка.");
            if (snapshot.BootId == boot && snapshot.Pending) pending.Add(snapshot);
        }
        return pending;
    }

    public HardwareChangeSnapshot? Load(string kind, string deviceId)
    {
        string path = PathFor(kind, deviceId);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Аппаратный снимок слишком большой.");
        HardwareChangeSnapshot snapshot = JsonSerializer.Deserialize<HardwareChangeSnapshot>(
            LicenseStore.UnprotectBytes(File.ReadAllBytes(path)))
            ?? throw new InvalidDataException("Снимок аппаратных параметров повреждён.");
        if (snapshot.Kind != kind || snapshot.DeviceId != deviceId)
            throw new InvalidDataException("Снимок относится к другому устройству.");
        // An earlier boot's limits must never be written to this boot.
        return snapshot.BootId == BootId ? snapshot : null;
    }

    public void Save(HardwareChangeSnapshot snapshot)
    {
        Directory.CreateDirectory(_root);
        string path = PathFor(snapshot.Kind, snapshot.DeviceId);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        snapshot.UpdatedAtUtc = DateTimeOffset.UtcNow;
        try
        {
            using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                output.Write(LicenseStore.ProtectBytes(JsonSerializer.SerializeToUtf8Bytes(snapshot)));
                output.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BootEnvironment
    {
        public Guid BootIdentifier;
        public int FirmwareType;
        public ulong BootFlags;
    }
    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int informationClass, out BootEnvironment information, int length, out int returnedLength);
}
