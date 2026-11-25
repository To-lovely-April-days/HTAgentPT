using System.Net;
using System.Text;
using HT.Agent.Application.Abstractions;
using HT.Agent.Application.Logic;
using HT.Agent.Infrastructure.Clients;

namespace HT.Agent.Tests;

/// <summary>模型服务临时出错（重启连不上、5xx、断流、卡住）不再让整轮中断：等一会儿重发这一轮。</summary>
[Collection("RetryDelays")]
public class ModelRetryTests
{
    private static readonly IReadOnlyList<ToolSpec> Tools = [new("search_documents", "搜", new { })];

    private sealed class Flaky(params Exception?[] failures) : IToolChatClient
    {
        public int Calls;
        public Task<ToolRoundResult> RunRoundAsync(IReadOnlyList<AgentMessage> messages, IReadOnlyList<ToolSpec> tools,
            ToolRoundOptions options, Func<string, Task>? onText, CancellationToken ct = default)
        {
            var i = Calls++;
            if (i < failures.Length && failures[i] is { } ex) throw ex;
            return Task.FromResult(new ToolRoundResult("答案", [], "stop"));
        }
    }

    private static async Task<(LoopResult R, int Calls)> Run(params Exception?[] failures)
    {
        var saved = AssistantLoop.RetryDelays;
        AssistantLoop.RetryDelays = [0, 0, 0];
        try
        {
            var model = new Flaky(failures);
            var r = await new AssistantLoop(model, new AssistantLoopOptions()).RunAsync([new("user", "q")], Tools,
                (_, _) => Task.FromResult(new ToolOutcome("x", "x", "搜索资料", "", null)), _ => Task.CompletedTask, CancellationToken.None);
            return (r, model.Calls);
        }
        finally { AssistantLoop.RetryDelays = saved; }
    }

    [Fact]
    public async Task 模型服务重启连不上_断流_卡住_等一会儿重发_照样答完()
    {
        var (r, calls) = await Run(new HttpRequestException("连不上"), new IOException("断流"), new ModelStalledException("卡住"));
        Assert.Equal("答案", r.Answer);
        Assert.Equal(4, calls);
        Assert.Equal(3, r.Steps.Count(s => s.Tool == "retry"));
    }

    [Fact]
    public async Task 五百错误重试_重试次数用完才报错()
    {
        var e500 = () => new HttpRequestException("500", null, HttpStatusCode.InternalServerError);
        await Assert.ThrowsAsync<HttpRequestException>(() => Run(e500(), e500(), e500(), e500()));
    }

    [Fact]
    public async Task 发的内容有问题的400不重试()
    {
        await Assert.ThrowsAsync<HttpRequestException>(() => Run(new HttpRequestException("400", null, HttpStatusCode.BadRequest)));
    }

    [Fact]
    public void 用户自己停下的不算临时故障()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.False(ModelErrors.IsTransient(new OperationCanceledException(), cts.Token));
        Assert.True(ModelErrors.IsTransient(new TaskCanceledException(), CancellationToken.None));
    }

    // ── 客户端：流中途断了 ──────────────────────────────

    private sealed class Sse(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") });
    }

    private sealed class Factory(HttpMessageHandler h) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(h, disposeHandler: false);
    }

    private sealed class Config : IRuntimeConfig
    {
        public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<int> GetIntAsync(string key, int fallback, CancellationToken ct = default) => Task.FromResult(fallback);
        public Task<double> GetDoubleAsync(string key, double fallback, CancellationToken ct = default) => Task.FromResult(fallback);
        public Task<string> GetStringAsync(string key, string fallback, CancellationToken ct = default) => Task.FromResult(fallback);
        public Task SetAsync(string key, string value, Guid? updatedBy, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
    }

    private static Task<ToolRoundResult> Call(string sse)
        => new OpenAiToolChatClient(new Factory(new Sse(sse)), new Config())
            .RunRoundAsync([new AgentMessage("user", "q")], [], new ToolRoundOptions(false), null, CancellationToken.None);

    [Fact]
    public async Task 输出中途断了_不当成答完了()
    {
        await Assert.ThrowsAsync<IOException>(() => Call("data: {\"choices\":[{\"delta\":{\"content\":\"写到一\"}}]}\n\n"));
    }

    [Fact]
    public async Task 正常结束的照常返回()
    {
        var r = await Call("data: {\"choices\":[{\"delta\":{\"content\":\"答完了\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n");
        Assert.Equal("答完了", r.Content);
        Assert.Equal("stop", r.FinishReason);
    }
}
