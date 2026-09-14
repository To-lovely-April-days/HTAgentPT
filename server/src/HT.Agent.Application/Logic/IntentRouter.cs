using System.Text.RegularExpressions;

namespace HT.Agent.Application.Logic;

/// <summary>意图路由（FR-4.1）：提问先判定类别——台账查询 / 文档生成 / 翻译 / 知识问答。
/// 规则式实现：确定性、可测试、不依赖模型在线；判定结果对用户可见并可手动纠正，
/// 所以宁可保守（拿不准就走知识问答），不做「聪明但不可预期」的抢分流。</summary>
public static class IntentRouter
{
    public const string Knowledge = "knowledge";
    public const string Ledger = "ledger";
    public const string Generate = "generate";
    public const string Translate = "translate";

    private static readonly Regex TranslatePattern = new(
        @"(翻译|译成|译为|英文版|中文版|译文|translate)", RegexOptions.Compiled);

    private static readonly Regex GeneratePattern = new(
        @"(生成|起草|拟一份|写一份|出一份|帮我写).{0,16}(方案|投标|标书|报价|合同|任务单)|(方案|投标书|报价单|合同).{0,4}(生成|起草)",
        RegexOptions.Compiled);

    // 台账信号分两类：直指台账的（项目编号、台账、合同金额、交付状态），与「历史项目 + 筛选维度」组合
    private static readonly Regex LedgerStrong = new(
        @"(台账|项目编号|P-\d{4}-\d+|合同金额|交付状态|已结项|哪些项目|做过.{0,12}(项目|设备)|历史项目)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>判定意图。vocabCustomers/vocabDevices 用于台账信号增强：
    /// 客户名或设备类型出现且问的是「做过/卖过/有没有」类盘点问题时倾向台账。</summary>
    public static string Classify(string question,
        IReadOnlyCollection<string> vocabCustomers,
        IReadOnlyCollection<string> vocabDevices)
    {
        if (string.IsNullOrWhiteSpace(question)) return Knowledge;
        if (TranslatePattern.IsMatch(question)) return Translate;
        if (GeneratePattern.IsMatch(question)) return Generate;
        if (LedgerStrong.IsMatch(question)) return Ledger;

        var mentionsCustomer = vocabCustomers.Any(c => c.Length >= 2 && question.Contains(c));
        var inventoryAsk = Regex.IsMatch(question, @"(做过|卖过|供过|交付过|合作过|有没有|几个|多少个|哪几)");
        if (mentionsCustomer && inventoryAsk) return Ledger;

        return Knowledge;
    }

    /// <summary>从台账类问题里抽取结构化筛选条件——抽取结果随表格一起返回（对用户可见，可改后重查）。</summary>
    public static LedgerFilters ExtractFilters(string question,
        IReadOnlyCollection<string> vocabCustomers,
        IReadOnlyCollection<string> vocabDevices)
    {
        string? customer = vocabCustomers
            .Where(c => c.Length >= 2 && question.Contains(c))
            .OrderByDescending(c => c.Length).FirstOrDefault();
        string? device = vocabDevices
            .Where(d => d.Length >= 2 && question.Contains(d))
            .OrderByDescending(d => d.Length).FirstOrDefault();

        int? yearFrom = null, yearTo = null;
        var yearMatches = Regex.Matches(question, @"(19|20)\d{2}").Select(m => int.Parse(m.Value)).ToList();
        if (yearMatches.Count == 1) yearFrom = yearTo = yearMatches[0];
        else if (yearMatches.Count >= 2) { yearFrom = yearMatches.Min(); yearTo = yearMatches.Max(); }
        // 「近三年」「最近两年」类相对区间由调用方结合当前年份换算（脚本层无时钟，服务层有）
        var m2 = Regex.Match(question, @"[近最][近]?([一二两三四五六七八九十\d]+)\s*年");
        int? recentYears = m2.Success ? ParseCnNumber(m2.Groups[1].Value) : null;

        return new LedgerFilters(customer, device, yearFrom, yearTo, recentYears);
    }

    private static int? ParseCnNumber(string s)
    {
        if (int.TryParse(s, out var n)) return n;
        return s switch
        {
            "一" => 1, "两" => 2, "二" => 2, "三" => 3, "四" => 4, "五" => 5,
            "六" => 6, "七" => 7, "八" => 8, "九" => 9, "十" => 10, _ => null
        };
    }
}

public record LedgerFilters(string? CustomerName, string? DeviceType, int? YearFrom, int? YearTo, int? RecentYears);
