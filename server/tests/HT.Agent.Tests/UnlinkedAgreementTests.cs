using HT.Agent.Infrastructure.Services;

namespace HT.Agent.Tests;

public class UnlinkedAgreementTests
{
    [Theory]
    [InlineData("技术协议", null, false, true)]     // 照旧：技术协议没编号要拦
    [InlineData("技术协议", "", false, true)]
    [InlineData("方案", null, false, true)]
    [InlineData("技术协议", null, true, false)]     // 历史资料说了暂不挂项目：放行
    [InlineData("技术协议", "XD-20251030-0159", false, false)]
    [InlineData("资料", null, false, false)]         // 本来就不用挂项目的
    public void 技术协议没编号要拦_说了暂不挂项目的放行(string category, string? projectNo, bool unlinked, bool blocked)
        => Assert.Equal(blocked, DocumentService.NeedsProject(category, projectNo, unlinked));
}
