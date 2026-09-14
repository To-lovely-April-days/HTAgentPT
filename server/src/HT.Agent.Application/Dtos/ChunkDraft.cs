namespace HT.Agent.Application.Dtos;

/// <summary>切分产物：待写入 chunk 表的一块。</summary>
public record ChunkDraft(int Seq, string? SectionPath, int? PageNo, string? Bbox, string Text);

/// <summary>切分参数（ConfigKeys.Chunk*，FR-1.4 可配置）。Overlap 默认为块长度一成（表 4-2 末段）。</summary>
public record ChunkingOptions(int TargetLength = 800, int Overlap = 80, int MinLength = 200);
