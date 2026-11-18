namespace HT.Agent.Application.Abstractions;

/// <summary>长期记忆：每个人只管自己的。</summary>
public interface IMemoryService
{
    Task<MemoryState> MineAsync(CancellationToken ct = default);
    Task<MemoryRow> AddAsync(string text, string kind, string source, CancellationToken ct = default);
    Task UpdateAsync(Guid id, string text, string kind, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task SetEnabledAsync(bool enabled, CancellationToken ct = default);
    /// <summary>全公司开关开着、本人没关、且本人用得上助手时为 true。</summary>
    Task<bool> ActiveAsync(CancellationToken ct = default);
    /// <summary>管理员看的：多少人在用、共多少条（看不到内容）。</summary>
    Task<(int Users, int Total)> StatsAsync(CancellationToken ct = default);
}

public record MemoryRow(Guid Id, string Kind, string Text, string Source, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public record MemoryState(bool GlobalOn, bool Enabled, int Max, IReadOnlyList<MemoryRow> Items);
