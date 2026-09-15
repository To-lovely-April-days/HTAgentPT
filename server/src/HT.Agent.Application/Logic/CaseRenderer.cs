using System.Text;

namespace HT.Agent.Application.Logic;

/// <summary>案例的固定模板渲染（FR-8.2）：渲染结果作为一个分块写入私有库，
/// 结构化字段随分块元数据。模板固定意味着渲染是纯函数——同一案例任何时候渲染出同一段文本，
/// 修改案例后重渲染即可原位替换分块内容。</summary>
public static class CaseRenderer
{
    public static string Render(
        string caseNo, string deviceModel, string? alarmCode, string phenomenon,
        string causeAnalysis, string steps, string? spareParts, string result,
        IReadOnlyDictionary<string, string>? extra = null)
    {
        var sb = new StringBuilder();
        sb.Append("【故障案例 ").Append(caseNo).Append("】设备型号：").Append(deviceModel);
        if (!string.IsNullOrWhiteSpace(alarmCode))
            sb.Append("　报警代码：").Append(alarmCode);
        sb.AppendLine();
        sb.Append("故障现象：").AppendLine(phenomenon.Trim());
        sb.Append("原因判断：").AppendLine(causeAnalysis.Trim());
        sb.Append("处理步骤：").AppendLine(steps.Trim());
        if (!string.IsNullOrWhiteSpace(spareParts))
            sb.Append("所用备件：").AppendLine(spareParts.Trim());
        sb.Append("处理结果：").Append(result.Trim());
        if (extra is { Count: > 0 })
        {
            sb.AppendLine();
            foreach (var (k, v) in extra.OrderBy(p => p.Key))
                if (!string.IsNullOrWhiteSpace(v))
                    sb.Append(k).Append('：').AppendLine(v.Trim());
        }
        return sb.ToString().TrimEnd();
    }
}
