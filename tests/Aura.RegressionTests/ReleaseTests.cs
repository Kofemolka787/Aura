using System.Net;
using System.Text;
using ColorForge.Services;
using Xunit;

public sealed class ReleaseTests
{
    [Fact] public async Task UpdateChecksHandleNoReleaseAndRejectUnrelatedUrls()
    {
        using var empty = new UpdateCheckService(new ReplyHandler(HttpStatusCode.NotFound, ""));
        Assert.False((await empty.CheckAsync(new Version(0, 11, 0))).Available);
        using var newer = new UpdateCheckService(new ReplyHandler(HttpStatusCode.OK,
            "{\"tag_name\":\"v0.12.0\",\"html_url\":\"https://github.com/Kofemolka787/Aura/releases/tag/v0.12.0\"}"));
        Assert.True((await newer.CheckAsync(new Version(0, 11, 0))).Available);
        using var unsafeLink = new UpdateCheckService(new ReplyHandler(HttpStatusCode.OK,
            "{\"tag_name\":\"v0.12.0\",\"html_url\":\"https://example.com/download\"}"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => unsafeLink.CheckAsync(new Version(0, 11, 0)));
    }
    private sealed class ReplyHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken t) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
