using System.Runtime.CompilerServices;
using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;

namespace HT.Agent.Infrastructure.Clients;

/// <summary>对话模型的运行时选择（E15 模型选择）：每次调用读 model.chat.provider——
/// stub 走内置演示应答，其余值走 OpenAI 兼容端点（地址/模型名/密钥同为运行时配置）。
/// 这样切换模型不用重启服务（FR-9.6 改后即时生效）；嵌入/重排/解析不随此切换，
/// 它们有各自的重建代价（换向量模型须全量重建，见 E15 警示）。</summary>
public class SwitchingChatClient(
    StubChatClient stub,
    OpenAiChatClient openAi,
    IRuntimeConfig config,
    Microsoft.Extensions.Configuration.IConfiguration appConfig) : IChatModelClient
{
    private async Task<IChatModelClient> PickAsync(CancellationToken ct)
    {
        // 未设 provider 时沿用部署期 Models:UseStubs 的语义，行为与旧版本一致
        var fallback = appConfig.GetSection("Models")["UseStubs"] == "true" ? "stub" : "openai";
        var provider = await config.GetStringAsync(ConfigKeys.ChatProvider, fallback, ct);
        return provider.Trim().Equals("stub", StringComparison.OrdinalIgnoreCase) ? stub : openAi;
    }

    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatTurn> messages, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var client = await PickAsync(ct);
        await foreach (var delta in client.StreamAsync(messages, ct))
            yield return delta;
    }

    public async Task<string> CompleteAsync(IReadOnlyList<ChatTurn> messages, CancellationToken ct = default)
        => await (await PickAsync(ct)).CompleteAsync(messages, ct);
}
