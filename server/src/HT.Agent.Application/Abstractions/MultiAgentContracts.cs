using HT.Agent.Domain.Entities;

namespace HT.Agent.Application.Abstractions;

/// <summary>
/// 多 Agent 对话编排。自然语言问答由多个角色共同完成：分析员、资料研究员、
/// 通用知识员和审校员并行给出意见，最后由汇总员生成对用户可见的答案。
/// 业务权限与工具调用仍由服务端控制，模型只提供意见，不直接访问数据库。
/// </summary>
public interface IMultiAgentQaService
{
    IAsyncEnumerable<QaEvent> RunAsync(
        QaRequest request,
        QaSession session,
        string initialIntent,
        CancellationToken ct = default);

    /// <summary>项目台账问题的多 Agent 协作：规划条件、执行受权限保护的台账工具、检索资料并汇总。</summary>
    IAsyncEnumerable<QaEvent> RunLedgerAsync(
        QaRequest request,
        QaSession session,
        CancellationToken ct = default);
}
