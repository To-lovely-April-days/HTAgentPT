using HT.Agent.Application.Abstractions;
using HT.Agent.Application.Logic;
using HT.Agent.Domain;
using HT.Agent.Infrastructure.Services;
using HT.Agent.Infrastructure.Workers;

namespace HT.Agent.Tests;

/// <summary>对话附件：按问题挑块、和知识库命中合并成可引用的依据、存取分块。</summary>
public class ChatAttachmentTests
{
    private static readonly Guid FileA = Guid.NewGuid();
    private static readonly Guid FileB = Guid.NewGuid();

    private static List<AttachmentRanker.Piece> Pieces() =>
    [
        new(FileA, "客户需求.docx", 1, "概述", 1, "本项目为尼龙66合成中试装置，甲方为华东理工大学。"),
        new(FileA, "客户需求.docx", 2, "设备要求", 2, "反应釜设计压力 5MPa，设计温度 350℃，材质 316L。"),
        new(FileA, "客户需求.docx", 3, "电气", 3, "现场为防爆区域，电机防爆等级 ExdIIBT4。"),
        new(FileB, "工艺包.pdf", 1, "流程", 1, "聚合后经冷凝器回收，冷凝器换热面积约 1m²。"),
    ];

    [Fact]
    public void 按问题挑块_相关的排前面()
    {
        var picked = AttachmentRanker.Pick(Pieces(), "电机的防爆等级要求是什么", take: 2);
        Assert.Equal(3, picked[0].Seq);
        Assert.Equal(FileA, picked[0].SourceId);

        var hx = AttachmentRanker.Pick(Pieces(), "冷凝器换热面积多大", take: 1);
        Assert.Equal(FileB, Assert.Single(hx).SourceId);
    }

    [Fact]
    public void 笼统的问题取各文件开头_多个文件轮流取()
    {
        var picked = AttachmentRanker.Pick(Pieces(), "总结一下这份文件", take: 2);
        Assert.Equal(2, picked.Count);
        Assert.Contains(picked, p => p.SourceId == FileA && p.Seq == 1);
        Assert.Contains(picked, p => p.SourceId == FileB && p.Seq == 1);
    }

    [Fact]
    public void 合并依据_附件排前_知识库没过阈值的不拿来当事实()
    {
        var kbChunk = new RetrievedChunk(42, Guid.NewGuid(), "历史协议", "第三章", 6, "设计压力 4MPa", 0.4, 0.2, Classification.Internal);
        var weak = new RetrievalResult(false, [kbChunk], [], 0.2, "q");
        var merged = MultiAgentQaService.MergeAttachments(weak, AttachmentRanker.Pick(Pieces(), "设计压力", take: 1));
        Assert.True(merged.AboveThreshold);
        var only = Assert.Single(merged.Chunks);
        Assert.Equal("客户需求.docx（本对话上传）", only.DocTitle);
        Assert.True(only.ChunkId < 0);          // 不是知识库分块：不去取原件和配图
        Assert.Equal(FileA, only.DocId);

        var strong = weak with { AboveThreshold = true };
        var both = MultiAgentQaService.MergeAttachments(strong, AttachmentRanker.Pick(Pieces(), "设计压力", take: 1));
        Assert.Equal(2, both.Chunks.Count);
        Assert.Equal(42, both.Chunks[1].ChunkId);

        Assert.Same(weak, MultiAgentQaService.MergeAttachments(weak, []));
    }

    [Fact]
    public void 分块存取_坏数据不抛()
    {
        var json = "[{\"seq\":1,\"section\":\"概述\",\"page\":1,\"text\":\"甲方为华东理工\"}]";
        var p = Assert.Single(AttachmentParseWorker.Pieces(FileA, "a.docx", json));
        Assert.Equal(("a.docx", "概述", "甲方为华东理工"), (p.FileName, p.Section, p.Text));
        Assert.Empty(AttachmentParseWorker.Pieces(FileA, "a.docx", "不是 JSON"));
        Assert.Empty(AttachmentParseWorker.Pieces(FileA, "a.docx", null));
    }

    [Fact]
    public void 限制_20MB_5个_常见格式()
    {
        Assert.Equal(20L * 1024 * 1024, AttachmentLimits.MaxBytes);
        Assert.Equal(5, AttachmentLimits.MaxPerMessage);
        Assert.Contains(".pdf", AttachmentLimits.Extensions);
        Assert.DoesNotContain(".doc", AttachmentLimits.Extensions);
    }
}
