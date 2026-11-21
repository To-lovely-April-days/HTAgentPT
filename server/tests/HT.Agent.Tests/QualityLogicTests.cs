using HT.Agent.Application.Logic;

namespace HT.Agent.Tests;

public class QualityLogicTests
{
    private static readonly EvalScoring.Card[] Cards =
    [
        new("高压釜维护手册", "高压釜维护手册.pdf", null, true),
        new("项目台账", null, "HT-2024-018", false),
    ];

    [Fact]
    public void GradesRetrievalAndPoints()
    {
        var s = EvalScoring.Grade(["维护手册"], ["HT-2024-018"], ["金属缠绕垫|缠绕垫", "每次开釜|开釜后更换"], false,
            "密封用的是缠绕垫，每次开釜后更换。", Cards, asked: false);
        Assert.True(s.RetrievalOk);
        Assert.True(s.AnswerOk);
        Assert.Empty(s.MissingPoints);
    }

    [Fact]
    public void ReportsMissingPointAndMissingDoc()
    {
        var s = EvalScoring.Grade(["选型表"], [], ["金属缠绕垫|缠绕垫", "开釜"], false, "用缠绕垫。", Cards, asked: false);
        Assert.False(s.RetrievalOk);
        Assert.False(s.AnswerOk);
        Assert.Equal(["开釜"], s.MissingPoints);
    }

    [Fact]
    public void NormalizesPunctuationAndWidth()
    {
        // 全角、空格、标点不影响比对
        var s = EvalScoring.Grade([], [], ["1.6 MPa"], false, "设计压力 １．６ＭＰａ。", [], asked: false);
        Assert.True(s.AnswerOk);
        Assert.Null(s.RetrievalOk);
    }

    [Fact]
    public void AskExpectationCountsTowardAnswer()
    {
        var missedAsk = EvalScoring.Grade([], [], [], true, "直接答了", [], asked: false);
        Assert.False(missedAsk.AnswerOk);
        var asked = EvalScoring.Grade([], [], [], true, "", [], asked: true);
        Assert.True(asked.AnswerOk);
        var nothingExpected = EvalScoring.Grade([], [], [], false, "随便", [], asked: false);
        Assert.Null(nothingExpected.AnswerOk);
    }

    [Fact]
    public void FactsExplainThresholdAndEmptySteps()
    {
        const string payload = """
        {"kind":"assistant",
         "searches":[{"query":"华理 哈氏釜 密封","top":0.58,"found":0},{"query":"别的","top":0.2,"found":0}],
         "steps":[{"tool":"search_documents","label":"搜索资料","result":"没有相关资料","status":"done"},
                  {"tool":"query_projects","label":"查项目","detail":"客户=华理","result":"0 个项目","status":"done"},
                  {"tool":"fetch_url","label":"打开网页","result":"超时","status":"error"}],
         "evidence":{"used":[],"other":[{"title":"x"},{"title":"y"}]},
         "verify":{"issues":[{"value":"1.6 MPa"}]}}
        """;
        var facts = IssueFacts.From(payload, 0.62);
        Assert.Contains(facts, f => f.Contains("0.58") && f.Contains("只低一点"));
        Assert.Contains(facts, f => f.Contains("0.20") && f.Contains("叫法不一样"));
        Assert.Contains(facts, f => f.Contains("查项目") && f.Contains("0 个项目"));
        Assert.DoesNotContain(facts, f => f.StartsWith("「搜索资料」")); // 搜资料的情况已经在上面按分数说了
        Assert.Contains(facts, f => f.Contains("打开网页") && f.Contains("出错"));
        Assert.Contains(facts, f => f.Contains("2 段资料") && f.Contains("没引用"));
        Assert.Contains(facts, f => f.Contains("1.6 MPa"));
    }

    [Fact]
    public void FactsToleratesBadPayload()
    {
        Assert.Empty(IssueFacts.From(null, 0.6));
        Assert.Empty(IssueFacts.From("not json", 0.6));
        Assert.Empty(IssueFacts.From("[]", 0.6));
    }

    [Fact]
    public void DescribesConfigChanges()
    {
        var prev = new Dictionary<string, string> { ["retrieval.score_threshold"] = "0.62", ["count.aliases"] = "30", ["count.docs"] = "100" };
        var cur = new Dictionary<string, string> { ["retrieval.score_threshold"] = "0.55", ["count.aliases"] = "42", ["count.docs"] = "100", ["parser.backend"] = "vlm" };
        var lines = ConfigDiff.Describe(prev, cur);
        Assert.Contains("「没找到」门槛 0.62 → 0.55", lines);
        Assert.Contains("别名 +12", lines);
        Assert.Contains("解析方式 （默认） → vlm", lines);
        Assert.DoesNotContain(lines, l => l.StartsWith("资料"));
        Assert.Empty(ConfigDiff.Describe(null, cur));
    }
}
