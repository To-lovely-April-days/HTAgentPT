using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;
using Microsoft.Extensions.Configuration;

namespace HT.Agent.Infrastructure.Clients;

/// <summary>部署期兜底口径：运行时 provider 未设置时的默认选择。
/// 布尔要 TryParse——JSON 配置里的 true 到字符串是 "True"，逐字比较 "true" 永远为假。</summary>
internal static class ModelSlotDefaults
{
    public static bool UseStubs(IConfiguration appConfig)
        => bool.TryParse(appConfig["Models:UseStubs"], out var b) && b;

    /// <summary>运行时配置里的布尔取值：TryParse（"True"/"true" 都认），取不出来用默认值。</summary>
    public static bool Flag(string? value, bool fallback)
        => bool.TryParse((value ?? "").Trim(), out var b) ? b : fallback;
}

/// <summary>向量化的运行时选择（E15 服务选择）：每次调用读 model.embedding.provider——
/// stub 走内置演示向量，其余值（siliconflow / local…）走兼容端点（地址/模型名/密钥同为运行时配置，
/// 在线与本地只是不同的地址与密钥，协议一致）。切换即换向量空间：一致性哨兵会亮，须配合全量重建。</summary>
public class SwitchingEmbeddingClient(
    StubEmbeddingClient stub, HttpEmbeddingClient http,
    IRuntimeConfig config, IConfiguration appConfig) : IEmbeddingClient
{
    private async Task<IEmbeddingClient> PickAsync(CancellationToken ct)
    {
        var fallback = ModelSlotDefaults.UseStubs(appConfig) ? "stub" : "openai";
        var provider = await config.GetStringAsync(ConfigKeys.EmbeddingProvider, fallback, ct);
        return provider.Trim().Equals("stub", StringComparison.OrdinalIgnoreCase) ? stub : http;
    }

    public async Task<EmbeddingBatch> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
        => await (await PickAsync(ct)).EmbedAsync(texts, ct);

    public async Task<string> CurrentTagAsync(CancellationToken ct = default)
        => await (await PickAsync(ct)).CurrentTagAsync(ct);
}

/// <summary>重排的运行时选择：stub 之外的值一律走 /rerank 兼容端点。切换无重建代价。</summary>
public class SwitchingRerankClient(
    StubRerankClient stub, HttpRerankClient http,
    IRuntimeConfig config, IConfiguration appConfig) : IRerankClient
{
    public async Task<IReadOnlyList<double>> ScoreAsync(string query, IReadOnlyList<string> passages, CancellationToken ct = default)
    {
        var fallback = ModelSlotDefaults.UseStubs(appConfig) ? "stub" : "openai";
        var provider = await config.GetStringAsync(ConfigKeys.RerankProvider, fallback, ct);
        var client = provider.Trim().Equals("stub", StringComparison.OrdinalIgnoreCase)
            ? (IRerankClient)stub : http;
        return await client.ScoreAsync(query, passages, ct);
    }
}

/// <summary>解析引擎的运行时选择：parser.provider = stub（演示，仅纯文本）/ mineru-local（本地容器）/
/// mineru-online（官方在线 API）/ http（通用契约）。未设置时沿用部署期 Models:Parser，
/// 再往后随 Models:UseStubs——老部署行为不变。未知取值按内容性错误报出，不静默回退演示桩。</summary>
public class SwitchingParserClient(
    StubParserClient stub, HttpParserClient http,
    MinerUParserClient mineruLocal, MinerUOnlineParserClient mineruOnline,
    OfficeDocxParser office,
    IRuntimeConfig config, IConfiguration appConfig) : IDocumentParserClient
{
    public async Task<ParsedDocument> ParseAsync(Stream file, string fileName, string contentType, CancellationToken ct = default)
    {
        // Word 文件本地解析：结构（标题层级、表格单元格）在文件里是现成的，交给版面引擎
        // 要先渲染再识别，慢且要出网。与 .txt/.md 同列，先于引擎选择处理；
        // 想改走引擎（如需要版面图片切分）把 parser.office_local 设为 false。
        if (OfficeDocxParser.Handles(fileName) &&
            ModelSlotDefaults.Flag(await config.GetStringAsync(ConfigKeys.ParserOfficeLocal, "true", ct), true))
            return await office.ParseAsync(file, fileName, contentType, ct);

        var deployed = (appConfig["Models:Parser"] ?? "").Trim().ToLowerInvariant();
        var fallback = deployed switch
        {
            "mineru" or "mineru-local" => "mineru-local",
            "mineru-online" => "mineru-online",
            "http" => "http",
            "stub" => "stub",
            _ => ModelSlotDefaults.UseStubs(appConfig) ? "stub" : "http"
        };
        var provider = (await config.GetStringAsync(ConfigKeys.ParserProvider, fallback, ct)).Trim().ToLowerInvariant();
        IDocumentParserClient client = provider switch
        {
            "stub" => stub,
            "http" => http,
            "mineru" or "mineru-local" => mineruLocal,
            "mineru-online" => mineruOnline,
            _ => throw new ParseContentException($"未知解析引擎配置 parser.provider={provider}（可选 stub / mineru-local / mineru-online / http）")
        };
        return await client.ParseAsync(file, fileName, contentType, ct);
    }
}
