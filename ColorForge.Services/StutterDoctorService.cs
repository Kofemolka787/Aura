using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using ColorForge.Models;

namespace ColorForge.Services;

public sealed class StutterDoctorService
{
    private static readonly Dictionary<int, (string Title, AuditSeverity Severity, string Detail)> KnownEvents = new()
    {
        [4101] = ("Сброс видеодрайвера", AuditSeverity.Critical, "Windows восстановила графический драйвер после зависания."),
        [17] = ("Исправленная ошибка PCIe", AuditSeverity.Caution, "WHEA зафиксировала аппаратную ошибку шины PCIe."),
        [18] = ("Аппаратная ошибка WHEA", AuditSeverity.Critical, "Проверь стабильность CPU/GPU и верни разгон к стандартным значениям."),
        [19] = ("Исправленная ошибка WHEA", AuditSeverity.Caution, "Система исправила аппаратную ошибку; при повторениях проверь стабильность."),
        [20] = ("Аппаратная ошибка WHEA", AuditSeverity.Critical, "Обнаружена некорректируемая аппаратная ошибка."),
        [129] = ("Сброс накопителя", AuditSeverity.Critical, "Контроллер накопителя перестал отвечать и был сброшен."),
        [153] = ("Повтор операции диска", AuditSeverity.Caution, "Windows повторяла операцию ввода-вывода на накопителе."),
        [41] = ("Неожиданное выключение", AuditSeverity.Caution, "Предыдущая работа Windows завершилась некорректно.")
    };

    public async Task<IReadOnlyList<AuditItem>> ScanAsync(BenchmarkResult? benchmark, CancellationToken cancellationToken)
    {
        List<AuditItem> result = new List<AuditItem>();
        if (benchmark != null)
        {
            AuditSeverity severity = benchmark.P99FrameTimeMs > 50 || benchmark.StutterCount > 10
                ? AuditSeverity.Caution
                : AuditSeverity.Good;
            result.Add(new AuditItem
            {
                Title = "Последний игровой тест",
                Detail = $"{benchmark.Summary} · скачков {benchmark.StutterCount}",
                Severity = severity
            });
        }

        IReadOnlyList<EventHit> hits = await ReadEventsAsync(cancellationToken);
        foreach (IGrouping<int, EventHit> group in hits.GroupBy(x => x.Id).OrderByDescending(x => KnownEvents[x.Key].Severity))
        {
            (string title, AuditSeverity severity, string detail) = KnownEvents[group.Key];
            DateTime newest = group.Max(x => x.TimeUtc).ToLocalTime();
            result.Add(new AuditItem
            {
                Title = title,
                Detail = $"{group.Count()} раз за 7 дней · последнее {newest:dd.MM HH:mm}. {detail}",
                Severity = severity
            });
        }

        if (hits.Count == 0)
        {
            result.Add(new AuditItem
            {
                Title = "Системный журнал",
                Detail = "За 7 дней не найдено TDR, WHEA и ошибок накопителя из проверяемого списка.",
                Severity = AuditSeverity.Good
            });
        }

        result.Add(new AuditItem
        {
            Title = "Метод проверки",
            Detail = "AURA читает только системные события Windows и последний тест PresentMon; файлы игр не изменяются.",
            Severity = AuditSeverity.Info
        });
        return result;
    }

    private static async Task<IReadOnlyList<EventHit>> ReadEventsAsync(CancellationToken cancellationToken)
    {
        const string query = "*[System[((EventID=4101 or EventID=17 or EventID=18 or EventID=19 or EventID=20 or EventID=129 or EventID=153 or EventID=41) and TimeCreated[timediff(@SystemTime) <= 604800000])]]";
        ProcessStartInfo start = new ProcessStartInfo
        {
            FileName = "wevtutil.exe",
            Arguments = $"qe System /q:\"{query}\" /c:80 /rd:true /f:xml",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        try
        {
            using Process process = Process.Start(start)!;
            string xml = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(xml))
                return Array.Empty<EventHit>();

            XDocument document = XDocument.Parse("<Events>" + xml + "</Events>");
            XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
            return document.Root!.Elements(ns + "Event")
                .Select(node =>
                {
                    XElement? system = node.Element(ns + "System");
                    int id = int.TryParse(system?.Element(ns + "EventID")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : 0;
                    string? stamp = system?.Element(ns + "TimeCreated")?.Attribute("SystemTime")?.Value;
                    DateTime time = DateTime.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime parsedTime)
                        ? parsedTime
                        : DateTime.UtcNow;
                    return new EventHit(id, time);
                })
                .Where(x => KnownEvents.ContainsKey(x.Id))
                .ToArray();
        }
        catch
        {
            return Array.Empty<EventHit>();
        }
    }

    private sealed record EventHit(int Id, DateTime TimeUtc);
}
