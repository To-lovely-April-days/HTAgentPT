using System.Text.Json;
using HT.Agent.Application.Logic;

namespace HT.Agent.Tests;

/// <summary>试用反馈修的几处：导出只留正文、说做了文件其实没做、Excel 排版问题。</summary>
public class FeedbackFixTests
{
    private const string Answer = """
        文件已重新生成（上方可下载），内容和上一版一致：

        - 标题 + 商务信息 + 10 行明细
        - 公式自动重算：每行金额 = 数量 × 单价

        如果这版还有显示问题，把具体哪一行告诉我；要按某台设备填入真实配置和价格也可以直接说。
        """;

    [Fact]
    public void 导出时去掉结尾问用户的话和指向界面的说明()
    {
        var clean = ExportText.Clean(Answer);
        Assert.DoesNotContain("告诉我", clean);
        Assert.DoesNotContain("上方可下载", clean);
        Assert.StartsWith("文件已重新生成，内容和上一版一致：", clean);
        Assert.Contains("公式自动重算", clean);
    }

    [Fact]
    public void 正文只有一段时不删_表格和标题不当收尾话()
    {
        Assert.Equal("如果压力超过 10 MPa，要按 GB/T 150 做分析设计。", ExportText.Clean("如果压力超过 10 MPa，要按 GB/T 150 做分析设计。"));
        Assert.False(ExportText.IsFollowUp("| 项目 | 如需调整 |"));
        Assert.False(ExportText.IsFollowUp("## 如有疑问的处理"));
        Assert.True(ExportText.IsFollowUp("需要我再按客户的工况调整吗？"));
    }

    [Fact]
    public void 文档标题用回答自己的标题_没有就不加()
    {
        Assert.Equal("50L 反应釜选型", ExportText.Heading("# 50L 反应釜选型\n\n正文"));
        Assert.Null(ExportText.Heading("正文第一段"));
    }

    [Theory]
    [InlineData("帮我重新生成一下文件", "文件已重新生成（上方可下载），内容和上一版一致", true)]
    [InlineData("做一个报价单模板", "报价单做好了，包含 10 行明细", true)]
    [InlineData("把税率改成 9%", "已经改好了报价单，税率改成 9%", true)]
    [InlineData("这个报价单的税率是多少", "上方报价单里税率是 13%", false)]
    [InlineData("帮我做一个报价单", "做报价单之前，想确认一下客户名称", false)]
    public void 认出说做好了文件的回答(string q, string a, bool expected) => Assert.Equal(expected, TaskFlow.ClaimsFile(q, a));

    [Fact]
    public void Excel排版问题_还有重做次数就先改_最后一次照样交()
    {
        var info = JsonDocument.Parse("""{"sheets":1,"layout":["报价单!D4「联系人/电话」比格子宽"]}""").RootElement;
        Assert.Single(MadeFiles.LayoutIssues(info));
        Assert.Empty(MadeFiles.LayoutIssues(JsonDocument.Parse("""{"sheets":1}""").RootElement));
        Assert.True(MadeFiles.FixLayoutFirst(0));
        Assert.True(MadeFiles.FixLayoutFirst(1));
        Assert.False(MadeFiles.FixLayoutFirst(MadeFiles.MaxAttempts - 1));
    }
}
