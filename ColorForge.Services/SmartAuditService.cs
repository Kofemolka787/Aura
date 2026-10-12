using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using ColorForge.Models;
using Microsoft.Win32;

namespace ColorForge.Services;

public sealed class SmartAuditService
{
    public Task<IReadOnlyList<AuditItem>> RunAsync(HardwareSnapshot snapshot, DisplayMode? displayMode)
    {
        return Task.Run<IReadOnlyList<AuditItem>>(() =>
        {
            List<AuditItem> items = new List<AuditItem>();

            Version os = Environment.OSVersion.Version;
            items.Add(new AuditItem
            {
                Title = "Windows",
                Detail = $"Windows {os.Major}.{os.Minor} · сборка {os.Build}",
                Severity = os.Build >= 22000 ? AuditSeverity.Good : AuditSeverity.Caution
            });

            MEMORYSTATUSEX memory = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(memory))
            {
                double ram = memory.ullTotalPhys / 1024d / 1024d / 1024d;
                items.Add(new AuditItem
                {
                    Title = "Оперативная память",
                    Detail = $"{ram:0} ГБ установлено",
                    Severity = ram >= 16 ? AuditSeverity.Good : AuditSeverity.Caution
                });
            }

            string systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            DriveInfo drive = new DriveInfo(systemRoot);
            double freeGb = drive.AvailableFreeSpace / 1024d / 1024d / 1024d;
            items.Add(new AuditItem
            {
                Title = "Свободное место",
                Detail = $"{freeGb:0.0} ГБ на системном диске",
                Severity = freeGb >= 20 ? AuditSeverity.Good : freeGb >= 8 ? AuditSeverity.Caution : AuditSeverity.Critical
            });

            if (displayMode != null)
            {
                items.Add(new AuditItem
                {
                    Title = "Активный дисплей",
                    Detail = $"{displayMode.Width} × {displayMode.Height} · {displayMode.RefreshRate} Гц",
                    Severity = displayMode.RefreshRate >= 120 ? AuditSeverity.Good : AuditSeverity.Info
                });
            }

            AddThermal(items, "Температура CPU", snapshot.CpuTemperatureC, 80, 90);
            AddThermal(items, "Температура GPU", snapshot.GpuTemperatureC, 78, 86);

            int? gameDvr = ReadDword(Registry.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled");
            items.Add(new AuditItem
            {
                Title = "Game DVR",
                Detail = gameDvr == 0 ? "Фоновая запись отключена" : "Фоновая запись может создавать лишнюю нагрузку",
                Severity = gameDvr == 0 ? AuditSeverity.Good : AuditSeverity.Caution
            });

            int? hags = ReadDword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode");
            items.Add(new AuditItem
            {
                Title = "Планирование GPU (HAGS)",
                Detail = hags == 2 ? "Включено" : hags == 1 ? "Отключено" : "Управляется Windows/драйвером",
                Severity = AuditSeverity.Info
            });

            bool vbs = ReadDword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\DeviceGuard", "EnableVirtualizationBasedSecurity") == 1;
            items.Add(new AuditItem
            {
                Title = "VBS / изоляция ядра",
                Detail = vbs ? "Защита активна; AURA не отключает её автоматически" : "Не обнаружено как активное",
                Severity = AuditSeverity.Info
            });

            bool pendingRestart = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired") != null
                                  || Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager")?.GetValue("PendingFileRenameOperations") != null;
            items.Add(new AuditItem
            {
                Title = "Перезагрузка Windows",
                Detail = pendingRestart ? "Есть изменения, ожидающие перезагрузки" : "Ожидающих изменений не найдено",
                Severity = pendingRestart ? AuditSeverity.Caution : AuditSeverity.Good
            });

            string plan = ReadPowerPlan();
            items.Add(new AuditItem
            {
                Title = "Схема питания",
                Detail = string.IsNullOrWhiteSpace(plan) ? "Не удалось прочитать" : plan,
                Severity = plan.Contains("High performance", StringComparison.OrdinalIgnoreCase)
                           || plan.Contains("Высокая производительность", StringComparison.OrdinalIgnoreCase)
                    ? AuditSeverity.Good
                    : AuditSeverity.Info
            });

            return items;
        });
    }

    private static void AddThermal(List<AuditItem> items, string title, double? value, double caution, double critical)
    {
        items.Add(new AuditItem
        {
            Title = title,
            Detail = value.HasValue ? $"{value:0} °C сейчас" : "Датчик недоступен без аппаратного драйвера",
            Severity = !value.HasValue ? AuditSeverity.Info
                : value >= critical ? AuditSeverity.Critical
                : value >= caution ? AuditSeverity.Caution
                : AuditSeverity.Good
        });
    }

    private static int? ReadDword(RegistryKey root, string path, string name)
    {
        try
        {
            using RegistryKey? key = root.OpenSubKey(path);
            object? value = key?.GetValue(name);
            return value == null ? null : Convert.ToInt32(value);
        }
        catch
        {
            return null;
        }
    }

    private static string ReadPowerPlan()
    {
        try
        {
            ProcessStartInfo start = new ProcessStartInfo("powercfg.exe", "/getactivescheme")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using Process process = Process.Start(start)!;
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);
            return output.Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private sealed class MEMORYSTATUSEX
    {
        public uint dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);
}
