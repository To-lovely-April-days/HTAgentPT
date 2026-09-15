namespace HT.Agent.Application.Abstractions;

/// <summary>对话模型客户端（表 8-2：OpenAI 兼容接口，地址与模型名可配置，须支持流式）。</summary>
public interface IChatModelClient
{
    /// <summary>流式生成。返回增量文本片段。</summary>
    IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatTurn> messages, CancellationToken ct = default);
    Task<string> CompleteAsync(IReadOnlyList<ChatTurn> messages, CancellationToken ct = default);
}

public record ChatTurn(string Role, string Content);

/// <summary>向量化模型客户端（表 8-2：批量向量化，批大小可配置）。
/// 向量与模型标签在同一次调用内返回：标签在调用开始时定格，避免配置切换瞬间
/// 产生打错标签的向量——标签是 FR-2.2 向量可复用性判定的依据，错标比缺标更糟。</summary>
public interface IEmbeddingClient
{
    Task<EmbeddingBatch> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default);

    /// <summary>当前配置下将会打在分块上的模型标签（与 EmbeddingBatch.ModelTag 同一口径）。
    /// 一致性哨兵与重建判定必须用这个，而不是裸模型名——标签含维度（如 bge-m3@1024），
    /// 拿裸名对比会把一致的库判成全量不一致。</summary>
    Task<string> CurrentTagAsync(CancellationToken ct = default);
}

public record EmbeddingBatch(IReadOnlyList<float[]> Vectors, string ModelTag);

/// <summary>重排序模型客户端（表 8-2：输入查询与候选分块，返回相关度分值）。</summary>
public interface IRerankClient
{
    Task<IReadOnlyList<double>> ScoreAsync(string query, IReadOnlyList<string> passages, CancellationToken ct = default);
}

/// <summary>文档解析引擎客户端（表 8-2：输入文件，输出结构化文本与位置信息）。
/// 引擎不可用时抛 ParserUnavailableException，任务置等待而非失败（10.3）。</summary>
public interface IDocumentParserClient
{
    Task<ParsedDocument> ParseAsync(Stream file, string fileName, string contentType, CancellationToken ct = default);
}

/// <summary>解析结果：保留章节层级、表格结构与页码位置（FR-1.3）。</summary>
public record ParsedDocument(IReadOnlyList<ParsedBlock> Blocks);

/// <summary>解析出的结构块。Kind：heading / paragraph / table_row / table。Level 仅标题有效。</summary>
public record ParsedBlock(string Kind, string Text, int? Level, int? PageNo, string? Bbox, string? TableHeader);

/// <summary>引擎不可用（连接失败、超时耗尽）。区别于内容性失败（文件损坏、格式不支持）。</summary>
public class ParserUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>内容性解析失败，携带具体原因（FR-1.7：不得仅显示失败二字）。</summary>
public class ParseContentException(string reason) : Exception(reason)
{
    public string Reason => Message;
}
