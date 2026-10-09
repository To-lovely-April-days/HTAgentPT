using System.Text;
using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;

namespace HT.Agent.Infrastructure.Services;

/// <summary>项目台账专员：规划由模型完成，执行始终走权限过滤后的只读项目服务。</summary>
public sealed class ProjectResearchAgent(
    IProjectQueryPlanner planner,
    IProjectService projects,
    ICurrentUser me) : IProjectResearchAgent
{
    public async Task<ProjectQueryPlan> PlanAsync(string question, string history, CancellationToken ct = default)
    {
        await MultiAgentQaService.ModelSlots.WaitAsync(ct);
        try
        {
            return await planner.PlanAsync(question, history, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ProjectQueryPlan(null, question, ["项目条件规划超时"]);
        }
        catch (HttpRequestException)
        {
            return new ProjectQueryPlan(null, question, ["项目条件规划服务暂时不可用"]);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ProjectQueryPlan(null, question, ["项目条件规划超时"]);
        }
        finally
        {
            MultiAgentQaService.ModelSlots.Release();
        }
    }

    public async Task<ProjectResearchResult> ExecuteAsync(ProjectQueryPlan plan, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!me.Permissions.Contains(PermissionKeys.ProjectSearch))
            return new ProjectResearchResult(null, "", "当前账号没有项目台账检索权限。");

        if (plan.UnresolvedConditions is { Count: > 0 })
            return new ProjectResearchResult(null, "",
                $"项目台账条件尚未明确：{string.Join("；", plan.UnresolvedConditions)}。本轮只继续检索企业资料，不查询整张台账。");

        if (plan.Query is null)
            return new ProjectResearchResult(null, "",
                "没有足够明确的项目筛选条件，暂不查询整张台账；请补充客户、年份、设备、地点或项目编号。");

        // 金额无权限时不能把条件静默丢掉再返回一批看似相关的项目。
        if (!me.Classifications.Contains(Classification.Confidential) &&
            (plan.Query.AmountMin is not null || plan.Query.AmountMax is not null))
            return new ProjectResearchResult(null, "",
                "该问题包含合同金额条件，但当前账号没有金额查看权限，因此未执行台账查询。");

        var request = plan.Query with { Limit = 21 };
        var result = await projects.SearchAsync(request, ct);
        var truncated = result.Rows.Count > 20;
        var rows = result.Rows.Take(20).ToList();
        ProjectDetail? detail = rows.Count == 1
            ? await projects.GetAsync(rows[0].ProjectNo, ct)
            : null;
        var isCandidate = !string.IsNullOrWhiteSpace(request.LocationHint);
        var note = isCandidate
            ? $"地点“{request.LocationHint}”仅按客户名或项目规格文本匹配，候选结果尚未核实项目实施地。"
            : "结果来自有权限的项目台账结构化筛选。";
        if (truncated) note += "结果超过 20 条，已截取前 20 条。";
        if (result.AmountFilterIgnored) note += "金额筛选因权限被忽略。";

        var filters = new ProjectResearchFilters(
            request.CustomerName, request.DeviceType, request.YearFrom, request.YearTo,
            request.Keyword, request.LocationHint, request.DeliveryStatus,
            request.AmountMin, request.AmountMax);
        var table = new ProjectResearchTable(filters, rows, result.AmountVisible, detail,
            note, isCandidate, truncated);
        return new ProjectResearchResult(table, BuildEvidence(rows, result.AmountVisible, isCandidate),
            rows.Count == 0 ? "没有找到符合这些筛选条件的项目记录。" : $"台账返回 {rows.Count} 条项目记录。");
    }

    private static string BuildEvidence(IReadOnlyList<ProjectRow> rows, bool amountVisible, bool candidate)
    {
        if (rows.Count == 0) return "项目台账没有返回符合条件的记录。";
        var sb = new StringBuilder();
        sb.AppendLine(candidate ? "以下是地点候选匹配，实施地尚未核实：" : "以下是项目台账记录：");
        foreach (var row in rows)
        {
            sb.Append($"项目 {row.ProjectNo}；客户 {row.CustomerName}；年份 {row.Year}；设备 {row.DeviceType}");
            if (!string.IsNullOrWhiteSpace(row.DeviceModel)) sb.Append($"；型号 {row.DeviceModel}");
            if (!string.IsNullOrWhiteSpace(row.SpecParams)) sb.Append($"；规格 {row.SpecParams}");
            if (row.DeliveryStatus is not null) sb.Append($"；交付 {row.DeliveryStatus}");
            if (amountVisible && row.ContractAmount is not null) sb.Append($"；合同金额 {row.ContractAmount}");
            sb.AppendLine("。");
        }
        return sb.ToString();
    }
}
