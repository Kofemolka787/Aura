using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ColorForge.Services;
public sealed record UpdateCheckResult(bool Available, string Message, Uri? ReleasePage = null);
public sealed class UpdateCheckService : IDisposable
{
    private readonly HttpClient _http;
    public UpdateCheckService(HttpMessageHandler? handler = null)
    {
        _http = handler == null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(15); _http.MaxResponseContentBufferSize = 1024 * 1024;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AURA/0.11");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }
    public async Task<UpdateCheckResult> CheckAsync(Version current, CancellationToken token = default)
    {
        using var response = await _http.GetAsync("https://api.github.com/repos/Kofemolka787/Aura/releases/latest", token);
        if (response.StatusCode == HttpStatusCode.NotFound) return new(false, "Опубликованных выпусков пока нет.");
        response.EnsureSuccessStatusCode();
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        string tag = json.RootElement.GetProperty("tag_name").GetString() ?? "";
        string url = json.RootElement.GetProperty("html_url").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out Version? latest)) return new(false, "Версия выпуска не распознана.");
        if (latest <= current) return new(false, "Установлена актуальная версия.");
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? page) || page.Scheme != "https" || page.Host != "github.com" || !string.IsNullOrEmpty(page.UserInfo)
            || !page.AbsolutePath.StartsWith("/Kofemolka787/Aura/releases/tag/", StringComparison.Ordinal))
            throw new InvalidOperationException("Ссылка выпуска не относится к репозиторию AURA.");
        return new(true, $"Доступна AURA {latest}. Ссылка на выпуск скопирована; установка выполняется вручную.", page);
    }
    public void Dispose() => _http.Dispose();
}
