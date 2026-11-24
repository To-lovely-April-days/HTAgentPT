namespace HT.Agent.Application.Abstractions;

/// <summary>会调用工具的对话模型（OpenAI 兼容 tools / tool_calls）。
/// 通用助手用它：模型自己决定查什么、查几次，系统只执行它要的工具并把结果交回去。</summary>
public interface IToolChatClient
{
    /// <summary>流式跑一轮。正文增量随到随出（OnText），工具调用在流结束时一次给全。
    /// tools 为空或 allowTools=false 时模型只能直接回答。</summary>
    Task<ToolRoundResult> RunRoundAsync(IReadOnlyList<AgentMessage> messages, IReadOnlyList<ToolSpec> tools,
        ToolRoundOptions options, Func<string, Task>? onText, CancellationToken ct = default);
}

/// <summary>一轮的参数。Effort：思考强度（low / medium / xhigh，空=模型默认）；TopP/TopK/PresencePenalty 空=不传、用模型服务的默认。</summary>
public record ToolRoundOptions(bool AllowTools = true, bool Think = false, int MaxTokens = 4096, double Temperature = 0.2,
    string? Effort = null, double? TopP = null, int? TopK = null, double? PresencePenalty = null);

/// <summary>一个工具的说明：名字、给模型看的用途说明、参数的 JSON Schema。</summary>
public record ToolSpec(string Name, string Description, object Parameters);

public record ToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>对话里的一条：system / user / assistant（可带工具调用）/ tool（某次调用的结果）。</summary>
public record AgentMessage(string Role, string? Content, IReadOnlyList<ToolCall>? ToolCalls = null, string? ToolCallId = null);

/// <summary>FinishReason=length：写到上限停了。Reasoning：模型的思考过程（推理解析器分出来的那部分）；ThinkSeconds：从发出到想完用了多久。</summary>
public record ToolRoundResult(string Content, IReadOnlyList<ToolCall> ToolCalls, string? FinishReason, string? Reasoning = null, double ThinkSeconds = 0);

/// <summary>模型端不支持工具调用（vLLM 没开 --enable-auto-tool-choice 之类）。调用方据此退回老问答流程。</summary>
public class ToolCallingUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>发过去的内容超出了模型服务的上下文长度（vLLM 返回 400 并报出上限和这次实际有多少）。
/// 回合循环据此按实际数字重估、把早先的内容压短再发一次。数字报不出来时为 0。</summary>
public class ContextOverflowException(string message, int contextTokens, int promptTokens)
    : HttpRequestException(message, null, System.Net.HttpStatusCode.BadRequest)
{
    public int ContextTokens { get; } = contextTokens;
    public int PromptTokens { get; } = promptTokens;

    /// <summary>认出模型服务报的「超出上下文」，取出上限和这次的输入长度。不是这类错误返回 null。</summary>
    public static ContextOverflowException? TryParse(string error)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(error, @"maximum context length|context length exceeded|context_length_exceeded|too many tokens|exceeds? the (?:model'?s? )?(?:maximum )?context", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return null;
        static int Num(string s, string pattern)
            => System.Text.RegularExpressions.Regex.Match(s, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var n) ? n : 0;
        return new ContextOverflowException($"超出模型上下文长度：{(error.Length > 300 ? error[..300] : error)}",
            Num(error, @"maximum context length is (\d+)"), Num(error, @"(?:prompt contains|messages resulted in) (?:at least )?(\d+)"));
    }
}

/// <summary>通用助手的一份依据（可被回答引用的最小单位）：资料切块、台账记录或对话附件片段。
/// N 是这次回答里的编号，回答写 [N]，右侧参考资料按 N 对应。</summary>
public record EvidenceItem(
    int N,
    string Kind,                // doc | ledger | attachment
    string Title,               // 文件标题 / 台账项目 / 附件名
    string Text,                // 切块全文（给模型看的、也给人看的）
    Guid? DocId = null,
    long? ChunkId = null,
    string? Section = null,
    int? PageNo = null,
    string? DocCategory = null,
    string? FileName = null,
    string? ProjectNo = null,
    string? Classification = null,
    double? Score = null,
    string? DocRef = null,      // 给模型用的短文档代号（D1、D2…），打开原文时用它
    string? Url = null,         // 网上来的依据：网页地址
    long? PriceId = null);      // 价格库里的一条价格

/// <summary>通用助手：一个问题进来，模型自己决定查什么，回答带编号引用、右侧参考资料和数字核对。</summary>
public interface IAssistantService
{
    /// <summary>事件：meta、step、delta、replace、evidence、verify、done、error。
    /// 模型端不支持工具调用时，在第一条正文之前抛 ToolCallingUnavailableException。</summary>
    IAsyncEnumerable<QaEvent> RunAsync(QaRequest req, Domain.Entities.QaSession session, CancellationToken ct = default);
}
