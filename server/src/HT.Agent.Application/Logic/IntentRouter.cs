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
    /// <summary>报修工单：进度、状态、派工。与案例的分界是「这一单办到哪了」而不是「这毛病怎么修」。</summary>
    public const string Ticket = "ticket";

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

    /// <summary>工单信号：问的是某一单的进度与状态，不是故障本身怎么修。
    /// 「报修」两边都有，所以工单判在案例之前——带进度/状态语境的归工单。</summary>
    private static readonly Regex TicketPattern = new(
        @"(工单|报修单|派工单)" +
        @"|(报修|维修|返修).{0,8}(进度|状态|到哪|怎么样|安排|派给谁|什么时候)" +
        @"|我的.{0,4}(工单|报修)" +
        @"|(待处理|未处理|挂起|已完成).{0,6}(工单|报修)",
        RegexOptions.Compiled);

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
        if (TicketPattern.IsMatch(question)) return Ticket;
        if (CasePattern.IsMatch(question)) return Case;
        if (!string.IsNullOrWhiteSpace(projectNoPattern) && SafeMatch(question, projectNoPattern)) return Ledger;

        // 盘点类问法 + （客户 或 设备类型）就算查台账。
        // 只认客户会漏掉「近三年做过哪些反应釜」这种不提客户的盘点。
        if (InventoryAsk.IsMatch(question) &&
            (vocabCustomers.Any(c => Mentions(question, c)) || vocabDevices.Any(d => Mentions(question, d))))
            return Ledger;

        return Knowledge;
    }

    private static readonly Regex InventoryAsk = new(
        @"(做过|卖过|买过|供过|采购过|交付过|合作过|用过|上过)" +
        @"|(有没有|有哪些|哪些|哪几|几台|几个|多少台|多少个|什么设备|哪款|哪种)",
        RegexOptions.Compiled);

    /// <summary>企业与机构名的后缀，去掉后是可用于匹配的主干。
    /// 词表里存「华东理工大学」而用户说「华东理工」（或反过来）都得认出来。</summary>
    private static readonly Regex OrgSuffix = new(
        @"(股份有限公司|有限责任公司|有限公司|科技有限公司|大学|学院|公司|集团|研究院|研究所|医院|工厂|厂|中心)$",
        RegexOptions.Compiled);

    /// <summary>词表项是否在提问里出现——全称、简称两头都认。</summary>
    public static bool Mentions(string question, string term)
    {
        if (string.IsNullOrWhiteSpace(term) || term.Length < 2) return false;
        if (question.Contains(term)) return true;
        var stem = OrgSuffix.Replace(term, "");
        return stem.Length >= 3 && stem.Length < term.Length && question.Contains(stem);
    }

    /// <summary>从台账类问题里抽取结构化筛选条件——抽取结果随表格一起返回（对用户可见，可改后重查）。</summary>
    public static LedgerFilters ExtractFilters(string question,
        IReadOnlyCollection<string> vocabCustomers,
        IReadOnlyCollection<string> vocabDevices)
    {
        string? customer = vocabCustomers
            .Where(c => Mentions(question, c))
            .OrderByDescending(c => c.Length).FirstOrDefault();
        string? device = vocabDevices
            .Where(d => Mentions(question, d))
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
