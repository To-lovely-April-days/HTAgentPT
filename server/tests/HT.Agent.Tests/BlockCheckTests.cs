using HT.Agent.Infrastructure.Services;

namespace HT.Agent.Tests;

public class BlockCheckTests
{
    [Theory]
    [InlineData("内压容器液压试验压力的最低值按下式确定", "4.6.2.2 液压试验\n内压容器液压试验压力的最低值，按下式确定：", true)]
    [InlineData("设备到达需方现场后 30 日内由需方组织验收", "第 8.2 条 设备到达需方现场后30日内，由需方组织验收；", true)]
    [InlineData("设备到达后 15 日内验收", "设备到达需方现场后30日内，由需方组织验收", false)]
    [InlineData("", "任何内容", false)]
    public void Quotes_must_appear_in_the_source(string quote, string source, bool ok)
        => Assert.Equal(ok, AssistantService.QuoteMatch(quote, source));
}
