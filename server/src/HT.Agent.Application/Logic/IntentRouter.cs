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
    /// <summary>故障案例：报警代码、故障现象、维修处理。</summary>
    public const string Case = "case";

    private static readonly Regex TranslatePattern = new(
        @"(翻译|译成|译为|英文版|中文版|译文|translate)", RegexOptions.Compiled);

    private static readonly Regex GeneratePattern = new(
        @"(生成|起草|拟一份|写一份|出一份|帮我写).{0,16}(方案|投标|标书|报价|合同|任务单)|(方案|投标书|报价单|合同).{0,4}(生成|起草)",
        RegexOptions.Compiled);

    /// <summary>案例信号：报警代码格式（E12、ER-03、Err5）、故障维修词，
    /// 或明说要看案例。与知识问答的分界是「出故障了怎么处理」而不是「参数是多少」。</summary>
    private static readonly Regex CasePattern = new(
        @"(故障|报警|报错|异常|不工作|不转|不升温|不制冷|漏液|漏油|跳闸|停机|卡死|报修)" +
        @"|(案例|维修记录|处理记录|以前.{0,6}(修|处理)过)" +
        @"|(E|ER|ERR|AL|ALM)[-_ ]?\d{1,3}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // 台账信号分两类：直指台账的（台账、合同金额、交付状态），与「历史项目 + 筛选维度」组合。
    // 项目编号格式因公司而异（10.4：编号规则不得硬编码），由配置注入。
    private static readonly Regex LedgerStrong = new(
        @"(台账|项目编号|合同金额|交付状态|已结项|哪些项目|做过.{0,12}(项目|设备)|历史项目)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>判定意图。vocabCustomers/vocabDevices 用于台账信号增强：
    /// 客户名或设备类型出现且问的是「做过/卖过/有没有」类盘点问题时倾向台账。
    /// projectNoPattern 为部署配置的项目编号正则（intent.project_no_pattern）。</summary>
    public static string Classify(string question,
        IReadOnlyCollection<string> vocabCustomers,
        IReadOnlyCollection<string> vocabDevices,
        string? projectNoPattern = null)
    {
        if (string.IsNullOrWhiteSpace(question)) return Knowledge;
        if (TranslatePattern.IsMatch(question)) return Translate;
        if (GeneratePattern.IsMatch(question)) return Generate;
        if (LedgerStrong.IsMatch(question)) return Ledger;
        if (CasePattern.IsMatch(question)) return Case;
        if (!string.IsNullOrWhiteSpace(projectNoPattern) && SafeMatch(question, projectNoPattern)) return Ledger;

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

    /// <summary>拆解翻译请求：往哪个方向译、要译的是哪段文字。
    /// 「请给我英文的」只是指令，正文得从同一句里剩下的部分找，找不到就交给调用方
    /// （拿上一条回答，或提示把文件发过来）——不能拿指令本身去翻译。</summary>
    public static TranslateAsk ParseTranslateAsk(string message)
    {
        var m = message.Trim();
        var toEn = Regex.IsMatch(m, @"(英文|英语|译成英|译为英|English|en)|英文版|翻成英", RegexOptions.IgnoreCase);
        var toZh = Regex.IsMatch(m, @"(中文|汉语|译成中|译为中|中文版|翻成中)");
        // 都没明说：按正文里中文字符的占比猜——中文多就译成英文
        var direction = toEn ? "zh2en" : toZh ? "en2zh" : null;
        var wantsFile = Regex.IsMatch(m, @"(文档|文件|附件|这份|这篇|docx|word)", RegexOptions.IgnoreCase);

        // 去掉指令片段，剩下的算正文
        var body = Regex.Replace(m,
            @"^(?:(?:请|帮我|麻烦|给我|我要|需要|想要)\s*)*(把|将)?\s*(这[段句篇份]?|以下|下面的?|上面的?|刚才的?|前面的?)?\s*" +
            @"(内容|文字|文本|段落|文档|文件|附件)?\s*(翻译|译)\s*(成|为|到)?\s*(中文|英文|英语|汉语)?\s*[：:，,。.]?",
            "", RegexOptions.IgnoreCase).Trim();
        body = Regex.Replace(body,
            @"^(?:(?:请|帮我|麻烦|给我|我要|需要|想要)\s*)*(一?[份个])?\s*(中文|英文|英语|汉语)(版|的)?\s*(文档|文件|版本)?\s*[：:，,。.]?",
            "").Trim();
        if (body.Length < 8) body = "";   // 剩不下什么就是纯指令

        direction ??= ChineseRatio(body.Length > 0 ? body : m) > 0.3 ? "zh2en" : "en2zh";
        return new TranslateAsk(direction, body.Length == 0 ? null : body, wantsFile);
    }

    private static double ChineseRatio(string s)
    {
        if (s.Length == 0) return 0;
        var cn = s.Count(c => c >= 0x4E00 && c <= 0x9FFF);
        return (double)cn / s.Length;
    }

    /// <summary>案例检索的关键词：去掉「有没有」「怎么处理」这类问法用词，
    /// 留下设备型号、报警代码与现象词——那才是能检索的东西。</summary>
    public static string CaseKeywords(string message)
    {
        var s = Regex.Replace(message.Trim(),
            @"(有没有|有无|查一下|查查|找一下|帮我找|看看|之前|以前|历史上|类似的?|相关的?|案例|记录|" +
            @"怎么(办|处理|修|解决)|如何(处理|解决|维修)|什么原因|为什么|是什么问题|该怎么|请问|吗|呢|\?|？)",
            " ", RegexOptions.IgnoreCase);
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    private static bool SafeMatch(string input, string pattern)
    {
        try { return Regex.IsMatch(input, pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50)); }
        catch (ArgumentException) { return false; }
        catch (RegexMatchTimeoutException) { return false; }
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

/// <summary>翻译请求的拆解结果。Direction：zh2en / en2zh。
/// Text 为要译的正文；说话人只下了指令没给正文时为 null，由调用方拿上一条回答或提示上传文件。</summary>
public record TranslateAsk(string Direction, string? Text, bool WantsFile);
