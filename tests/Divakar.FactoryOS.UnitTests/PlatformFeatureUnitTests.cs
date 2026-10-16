using System.Net;
using System.Text;
using System.Text.Json;
using Divakar.FactoryOS.Application.Assistant;
using Divakar.FactoryOS.Application.Reports;
using Divakar.FactoryOS.Infrastructure.Repositories;

namespace Divakar.FactoryOS.UnitTests;

public sealed class PlatformFeatureUnitTests
{
    private sealed class Capture(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Sent { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Sent = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task Claude_client_sends_the_messages_api_request_and_reads_the_text()
    {
        var handler = new Capture(HttpStatusCode.OK, """{"content":[{"type":"text","text":"OEE is 71%."}],"stop_reason":"end_turn"}""");
        var client = new ClaudeClient(new HttpClient(handler), new AiOptions { AnthropicApiKey = "test-key", Model = "claude-sonnet-5-5" });
        Assert.True(client.Enabled);

        var answer = await client.AskAsync("SYSTEM", "What is our OEE?", [new ChatTurn("user", "hi"), new ChatTurn("assistant", "Hello")], default);
        Assert.Equal("OEE is 71%.", answer);
        Assert.Equal("https://api.anthropic.com/v1/messages", handler.Request!.RequestUri!.ToString());
        Assert.Equal("test-key", handler.Request.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", handler.Request.Headers.GetValues("anthropic-version").Single());
        var body = JsonDocument.Parse(handler.Sent!).RootElement;
        Assert.Equal("claude-sonnet-5-5", body.GetProperty("model").GetString());
        Assert.Equal("SYSTEM", body.GetProperty("system").GetString());
        var messages = body.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(["user", "assistant", "user"], messages.Select(m => m.GetProperty("role").GetString()));
        Assert.Equal("What is our OEE?", messages[^1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Claude_client_is_off_without_a_key_and_reports_api_errors()
    {
        Assert.False(new ClaudeClient(new HttpClient(), new AiOptions()).Enabled);
        var handler = new Capture(HttpStatusCode.Unauthorized, """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""");
        var client = new ClaudeClient(new HttpClient(handler), new AiOptions { AnthropicApiKey = "bad" });
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.AskAsync("s", "q", [], default));
        Assert.Contains("invalid x-api-key", ex.Message);
    }

    [Fact]
    public void Csv_quotes_commas_and_quotes_and_starts_with_a_bom()
    {
        var report = new Report("energy", "Energy", new DateTime(2026, 10, 15), new DateTime(2026, 10, 16), [new Figure("Cost", "TWD 1,234")],
            ["Machine", "Note"], [["CNC \"A\"", "line 1, cell 2"]], []);
        var bytes = ReportService.ToCsv(report);
        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]);
        var text = Encoding.UTF8.GetString(bytes[3..]);
        Assert.Contains("Cost,\"TWD 1,234\"", text);
        Assert.Contains("\"CNC \"\"A\"\"\",\"line 1, cell 2\"", text);
    }

    [Fact]
    public void Built_in_assistant_says_what_is_outside_the_plan_and_gives_a_briefing_otherwise()
    {
        var empty = new FactorySnapshot(DateTime.UtcNow, null, null, null, null, null, null, null, null, null);
        Assert.Contains("not in your plan", BuiltIn.Answer("show me OEE", empty));
        Assert.Contains("not in your plan", BuiltIn.Answer("電費多少", empty));
        Assert.StartsWith("Here is the factory right now", BuiltIn.Answer("good morning", empty));
    }
}
