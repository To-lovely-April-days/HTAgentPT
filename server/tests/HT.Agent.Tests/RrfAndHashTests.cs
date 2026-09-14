using HT.Agent.Application.Logic;
using HT.Agent.Infrastructure.Auth;

namespace HT.Agent.Tests;

public class RrfFusionTests
{
    [Fact]
    public void 两路都命中的分块排名靠前()
    {
        var fused = RrfFusion.Fuse([1, 2, 3], [3, 4, 5], alpha: 0.5);
        Assert.Equal(3, fused[0].ChunkId); // 3 在两路都出现
    }

    [Fact]
    public void alpha为0时纯关键词路()
    {
        var fused = RrfFusion.Fuse([1, 2], [9, 8], alpha: 0);
        Assert.Equal(9, fused[0].ChunkId);
        Assert.Equal(0, fused.Single(f => f.ChunkId == 1).Score);
    }

    [Fact]
    public void alpha为1时纯向量路()
    {
        var fused = RrfFusion.Fuse([1, 2], [9, 8], alpha: 1);
        Assert.Equal(1, fused[0].ChunkId);
    }

    [Fact]
    public void 去重_同一分块两路分数相加()
    {
        var fused = RrfFusion.Fuse([7], [7], alpha: 0.5);
        var only = Assert.Single(fused);
        Assert.Equal(0.5 / 61 + 0.5 / 61, only.Score, 10);
    }
}

public class PasswordHasherTests
{
    private readonly Pbkdf2PasswordHasher _hasher = new();

    [Fact]
    public void 正确口令通过_错误口令失败()
    {
        var hash = _hasher.Hash("Zw@2025abc");
        Assert.True(_hasher.Verify("Zw@2025abc", hash));
        Assert.False(_hasher.Verify("Zw@2025abd", hash));
    }

    [Fact]
    public void 同一口令两次散列不同_盐随机()
    {
        Assert.NotEqual(_hasher.Hash("x"), _hasher.Hash("x"));
    }

    [Fact]
    public void 非法格式不抛异常只返回失败()
    {
        Assert.False(_hasher.Verify("x", "not-a-hash"));
    }
}
