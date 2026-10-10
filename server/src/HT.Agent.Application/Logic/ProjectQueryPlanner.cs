using System.Text.Json;
using System.Text.Json.Serialization;
using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;

namespace HT.Agent.Application.Logic;

/// <summary>项目台账查询规划器。模型只提出查询计划，永远不接触数据库或生成企业事实。</summary>
public sealed class ProjectQueryPlanner(IChatModelClient chat) : IProjectQueryPlanner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<ProjectQueryPlan> PlanAsync(string question, string history, CancellationToken ct = default)
    {
        question = question?.Trim() ?? "";
        var prompt = BuildPrompt(question, history ?? "");
        string raw;
        try
        {
            raw = await chat.CompleteAsync([
                new ChatTurn("system", prompt),
                new ChatTurn("user", question)], ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return SafeFailure(question, "项目条件规划超时");
        }
        catch (HttpRequestException)
        {
            return SafeFailure(question, "项目条件规划服务暂时不可用");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return SafeFailure(question, "项目条件规划超时");
        }

        PlannerPayload? payload;
        try
        {
            var json = ExtractJson(raw);
            payload = JsonSerializer.Deserialize<PlannerPayload>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return SafeFailure(question, "项目条件规划返回格式无效");
        }
        catch (ArgumentException)
        {
            return SafeFailure(question, "项目条件规划返回格式无效");
        }

        if (payload is null)
            return SafeFailure(question, "项目条件规划返回为空");

        var unresolved = (payload.UnresolvedConditions ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim()).Distinct().Take(12).ToList();
        // 地点/区域不是台账必备列。它应进入资料语义检索，由带项目编号的资料回填台账，
        // 不能因为模型提醒“没有独立地点字段”就把整条查询短路。
        if (!string.IsNullOrWhiteSpace(payload.LocationHint))
        {
            unresolved.RemoveAll(x => x.Contains("地点", StringComparison.Ordinal) ||
                                      x.Contains("区域", StringComparison.Ordinal) ||
                                      x.Contains("实施地", StringComparison.Ordinal));
        }
        var retrieval = string.IsNullOrWhiteSpace(payload.RetrievalQuery)
            ? question : payload.RetrievalQuery.Trim();
        var deliveryStatus = ParseStatus(payload.DeliveryStatus);
        if (!string.IsNullOrWhiteSpace(payload.DeliveryStatus) && deliveryStatus is null)
            unresolved.Add($"无法识别的交付状态“{payload.DeliveryStatus.Trim()}”");
        if (payload.YearFrom is < 1900 or > 2100 || payload.YearTo is < 1900 or > 2100)
            unresolved.Add("年份不在允许范围内");
        if (payload.YearFrom is not null && payload.YearTo is not null && payload.YearFrom > payload.YearTo)
            unresolved.Add("年份范围前后顺序不一致");
        if (payload.AmountMin is < 0 || payload.AmountMax is < 0 ||
            (payload.AmountMin is not null && payload.AmountMax is not null && payload.AmountMin > payload.AmountMax))
            unresolved.Add("合同金额范围无效");
        var hasCondition = HasCondition(payload);
        // allowRecent 只能代表「明确要求最近项目」且不能和任何筛选条件混用。
        // 是否明确由模型依据原问题判断；这里至少阻止带筛选的“全表”查询。
        var allowRecent = payload.AllowRecent && !hasCondition && unresolved.Count == 0 &&
                          IsExplicitRecent(question);
        if (!hasCondition && !allowRecent)
            return new ProjectQueryPlan(null, retrieval, unresolved, false);
        if (unresolved.Count > 0)
            return new ProjectQueryPlan(null, retrieval, unresolved, allowRecent);

        var query = new ProjectSearchRequest(
            CustomerName: Clean(payload.Customer),
            YearFrom: payload.YearFrom,
            YearTo: payload.YearTo,
            DeviceType: Clean(payload.DeviceType),
            AmountMin: payload.AmountMin,
            AmountMax: payload.AmountMax,
            DeliveryStatus: deliveryStatus,
            Keyword: Clean(payload.Keyword),
            Limit: 21,
            LocationHint: Clean(payload.LocationHint));
        return new ProjectQueryPlan(query, retrieval, unresolved, allowRecent);
    }

    internal static string ExtractJson(string raw)
    {
        var text = raw?.Trim() ?? "";
        if (text.StartsWith("```") && text.EndsWith("```"))
        {
            var firstNewLine = text.IndexOf('\n');
            text = firstNewLine >= 0 ? text[(firstNewLine + 1)..^3].Trim() : text[3..^3].Trim();
        }
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) throw new JsonException("JSON object not found");
        return text[start..(end + 1)];
    }

    private static ProjectQueryPlan SafeFailure(string question, string detail)
        => new(null, question, [detail], false);

    private static bool HasCondition(PlannerPayload p)
        => !string.IsNullOrWhiteSpace(p.Customer) || !string.IsNullOrWhiteSpace(p.DeviceType) ||
           p.YearFrom is not null || p.YearTo is not null || !string.IsNullOrWhiteSpace(p.Keyword) ||
           !string.IsNullOrWhiteSpace(p.LocationHint) || !string.IsNullOrWhiteSpace(p.DeliveryStatus) ||
           p.AmountMin is not null || p.AmountMax is not null;

    private static bool IsExplicitRecent(string question)
        => question.Contains("最近", StringComparison.Ordinal) ||
           question.Contains("最新", StringComparison.Ordinal) ||
           question.Contains("近期", StringComparison.Ordinal);

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DeliveryStatus? ParseStatus(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (Enum.TryParse<DeliveryStatus>(value.Trim(), true, out var parsed)) return parsed;
        return value.Trim() switch
        {
            "进行中" or "在途" or "未交付" => DeliveryStatus.InProgress,
            "已交付" or "交付" => DeliveryStatus.Delivered,
            "已结项" or "已完成" or "关闭" => DeliveryStatus.Closed,
            _ => null
        };
    }

    private static string BuildPrompt(string question, string history) => $"""
你是企业项目台账查询规划专员。只输出一个 JSON 对象，不要 Markdown，不要解释。
字段必须包含：customer、deviceType、yearFrom、yearTo、keyword、locationHint、deliveryStatus、amountMin、amountMax、allowRecent、unresolvedConditions、retrievalQuery。
把用户明确提出的每个筛选条件保留下来；无法安全映射的条件放入 unresolvedConditions，绝不能忽略。
地点、区域和“哪里做过”是语义条件，放入 locationHint 并保留在 retrievalQuery；不要因为台账没有独立地点列而写入 unresolvedConditions。
资料检索必须优先寻找带项目编号的企业资料，后续会把已授权资料关联回台账。没有项目编号的资料只能作为待核实线索，不能生成项目记录。
区域词可以在 retrievalQuery 中补充你理解的相关地名或企业常用表达，以提高资料召回；这些扩展只是检索提示，不能直接当作项目地点事实。
不要把地点直接当作客户名；不能根据大学/公司名称单独推断项目实施地。
只有用户明确要求“最近/最新/近期项目”，且没有任何筛选条件时，allowRecent 才能为 true；否则为 false。
amountMin/amountMax 是合同金额筛选，不能猜测单位或数值。
retrievalQuery 保留用户问题中适合资料检索的原意。
keyword 仅用于项目编号、设备型号或规格参数；地点和区域不要重复写入 keyword。
若用户使用“华东区域”等区域表达，可在 retrievalQuery 中保留原词并补充模型理解的同义地理表述，仅用于扩大资料召回，不代表任何项目事实。
若不是明确的项目台账查询，也应把无法确认的条件写入 unresolvedConditions，避免全表查询。
最近对话（仅用于理解指代，不是事实）：
{history}
当前问题：{question}
""";

    private sealed class PlannerPayload
    {
        [JsonPropertyName("customer")] public string? Customer { get; set; }
        [JsonPropertyName("deviceType")] public string? DeviceType { get; set; }
        [JsonPropertyName("yearFrom")] public int? YearFrom { get; set; }
        [JsonPropertyName("yearTo")] public int? YearTo { get; set; }
        [JsonPropertyName("keyword")] public string? Keyword { get; set; }
        [JsonPropertyName("locationHint")] public string? LocationHint { get; set; }
        [JsonPropertyName("deliveryStatus")] public string? DeliveryStatus { get; set; }
        [JsonPropertyName("amountMin")] public decimal? AmountMin { get; set; }
        [JsonPropertyName("amountMax")] public decimal? AmountMax { get; set; }
        [JsonPropertyName("allowRecent")] public bool AllowRecent { get; set; }
        [JsonPropertyName("unresolvedConditions")] public List<string>? UnresolvedConditions { get; set; }
        [JsonPropertyName("retrievalQuery")] public string? RetrievalQuery { get; set; }
    }
}
