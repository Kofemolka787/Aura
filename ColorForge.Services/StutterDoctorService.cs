using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using ColorForge.Models;

namespace ColorForge.Services;

public sealed class StutterDoctorService
{
    private const int Limit = 5000;
    private readonly IExternalToolRunner _runner;
    public StutterDoctorService(IExternalToolRunner? runner = null) => _runner = runner ?? new ExternalToolRunner();
    private static (string Title, AuditSeverity Severity, string Detail)? Describe(string provider, int id)
    {
        if (provider.Equals("Microsoft-Windows-WHEA-Logger", StringComparison.OrdinalIgnoreCase))
            return id switch
            {
                17 => ("Исправленная ошибка PCIe", AuditSeverity.Caution, "WHEA сообщила об исправленной аппаратной ошибке PCIe."),
                18 or 20 => ("Аппаратная ошибка WHEA", AuditSeverity.Critical, "Проверь устойчивость CPU/GPU и исходные параметры."),
                19 => ("Исправленная ошибка WHEA", AuditSeverity.Caution, "Нужна проверка подробностей события и повторений."),
                _ => null
            };
        if (id == 4101 && provider.Equals("Display", StringComparison.OrdinalIgnoreCase))
            return ("Сброс видеодрайвера", AuditSeverity.Critical, "Windows восстановила графический драйвер.");
        if (id == 41 && provider.Equals("Microsoft-Windows-Kernel-Power", StringComparison.OrdinalIgnoreCase))
            return ("Некорректное завершение Windows", AuditSeverity.Caution, "Событие не определяет причину отключения.");
        if (provider.Equals("disk", StringComparison.OrdinalIgnoreCase) || provider.Equals("storahci", StringComparison.OrdinalIgnoreCase)
            || provider.Equals("stornvme", StringComparison.OrdinalIgnoreCase) || provider.Equals("storport", StringComparison.OrdinalIgnoreCase))
            return id switch
            {
                129 => ("Сброс накопителя", AuditSeverity.Critical, "Контроллер накопителя перестал отвечать."),
                153 => ("Повтор операции диска", AuditSeverity.Caution, "Проверь накопитель, контроллер и время события."),
                _ => null
            };
        return null;
    }

    public async Task<IReadOnlyList<AuditItem>> ScanAsync(BenchmarkResult? benchmark, CancellationToken token)
    {
        List<AuditItem> items = new();
        if (benchmark != null)
            items.Add(new() { Title = "Последний игровой тест", Detail = benchmark.Summary + $" · скачков {benchmark.StutterCount}",
                Severity = benchmark.P99FrameTimeMs > 50 ? AuditSeverity.Caution : AuditSeverity.Info });
        const string query = "*[System[TimeCreated[timediff(@SystemTime) <= 604800000] and ((Provider[@Name='Microsoft-Windows-WHEA-Logger'] and (EventID=17 or EventID=18 or EventID=19 or EventID=20)) or (Provider[@Name='Display'] and EventID=4101) or (Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41) or ((Provider[@Name='disk'] or Provider[@Name='storahci'] or Provider[@Name='stornvme'] or Provider[@Name='storport']) and (EventID=129 or EventID=153)))]]";
        try
        {
            ToolResult result = await _runner.RunAsync(Path.Combine(Environment.SystemDirectory, "wevtutil.exe"),
                new[] { "qe", "System", "/q:" + query, "/c:" + Limit, "/rd:true", "/f:xml" }, TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
            if (result.ExitCode != 0) throw new InvalidOperationException("Журнал не прочитан (" + result.ExitCode + "): " + result.Error.Trim());
            XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
            string xml = result.Output.Trim().TrimStart('\uFEFF');
            if (xml.StartsWith("<?xml", StringComparison.Ordinal)) xml = xml[(xml.IndexOf("?>", StringComparison.Ordinal) + 2)..];
            using XmlReader reader = XmlReader.Create(new StringReader("<Events>" + xml + "</Events>"),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 });
            XElement[] events = XDocument.Load(reader).Descendants(ns + "Event").ToArray();
            int recognized = 0;
            List<AuditItem> recognizedItems = new();
            foreach (XElement node in events)
            {
                XElement? system = node.Element(ns + "System");
                string provider = system?.Element(ns + "Provider")?.Attribute("Name")?.Value ?? string.Empty;
                if (!int.TryParse(system?.Element(ns + "EventID")?.Value, out int id)) continue;
                var description = Describe(provider, id);
                if (description == null) continue;
                recognized++;
                string timestamp = system?.Element(ns + "TimeCreated")?.Attribute("SystemTime")?.Value ?? string.Empty;
                string when = DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
                    ? date.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss") : "время не указано";
                string relation = benchmark != null && date.UtcDateTime >= benchmark.RecordedAt.ToUniversalTime()
                    && date.UtcDateTime <= benchmark.RecordedAt.ToUniversalTime().AddSeconds(benchmark.DurationSeconds)
                    ? " · в интервале последнего замера" : string.Empty;
                recognizedItems.Add(new() { Title = description.Value.Title, Source = provider + " / " + id, TimestampUtc = date,
                    Detail = when + " · " + provider + " / " + id + relation + ". " + description.Value.Detail,
                    Severity = description.Value.Severity });
            }
            foreach (var group in recognizedItems.GroupBy(x => x.Source))
            {
                var first = group.First();
                items.Add(new() { Title = first.Title, Source = first.Source, TimestampUtc = first.TimestampUtc, Severity = first.Severity,
                    Occurrences = group.Count(), Detail = $"Повторений: {group.Count()} · последнее: " + first.Detail });
            }
            if (recognized == 0)
                items.Add(new() { Title = "Системный журнал", Detail = "Журнал прочитан: проверяемых событий за 7 дней не найдено.", Severity = AuditSeverity.Good });
            if (events.Length >= Limit)
                items.Add(new() { Title = "Ограниченная выборка", Detail = $"Показаны последние {Limit} событий; это не полный итог за 7 дней.", Severity = AuditSeverity.Caution });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            AppLogService.Error("System event analysis failed.", exception);
            items.Add(new() { Title = "Журнал не проверен", Detail = exception.Message, Severity = AuditSeverity.Caution });
        }
        items.Add(new() { Title = "Границы диагностики", Detail = "Событие в журнале само по себе не доказывает причину игрового статтера.", Severity = AuditSeverity.Info });
        return items;
    }
}
