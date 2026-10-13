using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ColorForge.Models;
using Microsoft.Win32;

namespace ColorForge.Services;

public sealed class SmartAuditService
{
    private readonly IExternalToolRunner _runner;
    public SmartAuditService(IExternalToolRunner? runner = null) => _runner = runner ?? new ExternalToolRunner();
    public Task<IReadOnlyList<AuditItem>> RunAsync(HardwareSnapshot snapshot, DisplayMode? displayMode, CancellationToken token = default)
    {
        return Task.Run<IReadOnlyList<AuditItem>>(async () =>
        {
            token.ThrowIfCancellationRequested();
            List<AuditItem> items = new List<AuditItem>();

            Version os = Environment.OSVersion.Version;
            items.Add(new AuditItem
            {
                Title = "Windows",
                Detail = $"Windows {os.Major}.{os.Minor} · сборка {os.Build}",
                Severity = os.Build >= 17763 ? AuditSeverity.Good : AuditSeverity.Caution
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
                Detail = gameDvr == 0 ? "Фоновая запись отключена" : gameDvr == null ? "Значение не прочитано; состояние не подтверждено" : "Фоновая запись может создавать лишнюю нагрузку",
                Severity = gameDvr == 0 ? AuditSeverity.Good : gameDvr == null ? AuditSeverity.Info : AuditSeverity.Caution
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
                Detail = vbs ? "VBS включена в конфигурации; фактический запуск здесь не проверяется" : "VBS не обнаружена в проверенной конфигурации",
                Severity = AuditSeverity.Info
            });

            using RegistryKey? restartKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            using RegistryKey? sessionManager = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
            bool pendingRestart = restartKey != null || sessionManager?.GetValue("PendingFileRenameOperations") != null;
            items.Add(new AuditItem
            {
                Title = "Перезагрузка Windows",
                Detail = pendingRestart ? "Есть изменения, ожидающие перезагрузки" : "Ожидающих изменений не найдено",
                Severity = pendingRestart ? AuditSeverity.Caution : AuditSeverity.Good
            });

            string plan = await ReadPowerPlanAsync(token).ConfigureAwait(false);
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
        }, token);
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

    private async Task<string> ReadPowerPlanAsync(CancellationToken token)
    {
        try
        {
            var result = await _runner.RunAsync(Path.Combine(Environment.SystemDirectory, "powercfg.exe"), new[] { "/getactivescheme" }, TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
            return result.ExitCode == 0 ? result.Output.Trim() : "Не подтверждено: " + result.Error.Trim();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            return "Не подтверждено: " + exception.Message;
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
