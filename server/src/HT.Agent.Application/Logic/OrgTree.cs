namespace HT.Agent.Application.Logic;

/// <summary>组织架构树上的「谁审谁」。纯计算，不碰数据库，方便测。
///
/// 规则：
/// - 主管（任一部门的主管）自己上传的直接通过；
/// - 员工的事项交给离他最近的、设了主管的上级部门（从自己部门开始往上找）；
/// - 主管能处理他所管部门及全部下级部门里的人提交的事项（下级部门另有主管也一样能处理，兜底）；
/// - 没有部门、或一路往上都没设主管的，只有系统管理员能处理。</summary>
public static class OrgTree
{
    public record Node(Guid Id, Guid? ParentId, Guid? HeadUserId, string Name);

    /// <summary>这个人是不是某个部门的主管。</summary>
    public static bool IsHead(IEnumerable<Node> nodes, Guid userId) => nodes.Any(n => n.HeadUserId == userId);

    /// <summary>approver 管得到的部门：他当主管的部门及其全部下级部门。</summary>
    public static HashSet<Guid> ScopeOf(IReadOnlyList<Node> nodes, Guid approverId)
    {
        var children = nodes.Where(n => n.ParentId is not null).ToLookup(n => n.ParentId!.Value);
        var scope = new HashSet<Guid>();
        var stack = new Stack<Guid>(nodes.Where(n => n.HeadUserId == approverId).Select(n => n.Id));
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!scope.Add(id)) continue; // 防环
            foreach (var c in children[id]) stack.Push(c.Id);
        }
        return scope;
    }

    /// <summary>这个人提交的事项归谁审：返回审批人；null 表示没有主管可找（交系统管理员）。
    /// 本人就是主管时也返回 null——调用方先用 IsHead 判断直接通过。</summary>
    public static Node? ApproverDeptFor(IReadOnlyList<Node> nodes, Guid? departmentId, Guid userId)
    {
        var byId = nodes.ToDictionary(n => n.Id);
        var seen = new HashSet<Guid>();
        var cur = departmentId;
        while (cur is not null && byId.TryGetValue(cur.Value, out var n) && seen.Add(n.Id))
        {
            if (n.HeadUserId is not null && n.HeadUserId != userId) return n;
            cur = n.ParentId;
        }
        return null;
    }

    /// <summary>把 dept 挂到 newParent 下会不会成环（挂到自己或自己的下级下面）。</summary>
    public static bool WouldCycle(IReadOnlyList<Node> nodes, Guid dept, Guid? newParent)
    {
        if (newParent is null) return false;
        if (newParent == dept) return true;
        var byId = nodes.ToDictionary(n => n.Id);
        var cur = newParent;
        var seen = new HashSet<Guid>();
        while (cur is not null && byId.TryGetValue(cur.Value, out var n) && seen.Add(n.Id))
        {
            if (n.Id == dept) return true;
            cur = n.ParentId;
        }
        return false;
    }

    /// <summary>名单里和 name 相似的客户（去掉「有限公司」「股份」这类后缀后互相包含）。用来提醒别重复建。</summary>
    public static IReadOnlyList<string> SimilarNames(string name, IEnumerable<string> existing, int take = 3)
    {
        static string Core(string s)
        {
            var t = s.Replace(" ", "").Replace("（", "(").Replace("）", ")");
            foreach (var suffix in new[] { "股份有限公司", "有限责任公司", "有限公司", "集团", "股份", "公司", "科技" })
                t = t.Replace(suffix, "");
            return t;
        }
        var core = Core(name);
        if (core.Length < 2) return [];
        return existing.Where(e => !string.Equals(e, name, StringComparison.Ordinal))
            .Where(e => { var c = Core(e); return c.Length >= 2 && (c.Contains(core) || core.Contains(c)); })
            .Take(take).ToList();
    }
}
