using HT.Agent.Application.Abstractions;
using HT.Agent.Application.Logic;
using HT.Agent.Domain;

namespace HT.Agent.Tests;

public class RetrievalEvidenceFormatterTests
{
    [Fact]
    public void 保留连续表格分块而不是在旧的小预算处截断()
    {
        var chunks = Enumerable.Range(1, 4)
            .Select(i => new RetrievedChunk(
                i, Guid.NewGuid(), "设备说明书", "通讯地址附表", i <= 2 ? 31 : 32,
                $"表格分块-{i}: " + new string('表', 800), 0.1, 0.9, Classification.Internal))
            .ToList();

        var result = new RetrievalResult(true, chunks, [], 0.9, "通信地址");
        var evidence = RetrievalEvidenceFormatter.Format(result);

        Assert.Contains("表格分块-1", evidence);
        Assert.Contains("表格分块-3", evidence);
        Assert.DoesNotContain("旧的小预算", evidence);
    }

    [Fact]
    public void 没有可信资料时保留检索提示()
    {
        var result = new RetrievalResult(false, [], [], 0, "通信地址", "向量服务暂不可用");

        var evidence = RetrievalEvidenceFormatter.Format(result);

        Assert.Contains("向量服务暂不可用", evidence);
        Assert.Contains("没有找到达到可信阈值", evidence);
    }
}
