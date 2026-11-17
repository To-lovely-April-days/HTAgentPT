using HT.Agent.Application.Logic;
using static HT.Agent.Application.Logic.OrgTree;

namespace HT.Agent.Tests;

public class OrgTreeTests
{
    private static readonly Guid Boss = Guid.NewGuid(), PreHead = Guid.NewGuid(), EastHead = Guid.NewGuid(), Staff = Guid.NewGuid();
    private static readonly Guid Sales = Guid.NewGuid(), Pre = Guid.NewGuid(), East = Guid.NewGuid(), South = Guid.NewGuid(), Lone = Guid.NewGuid();

    // 销售中心(Boss) → 售前部(PreHead) → 华东组(EastHead) / 华南组(无主管)；另有一个没主管的孤立部门
    private static readonly IReadOnlyList<Node> Tree =
    [
        new(Sales, null, Boss, "销售中心"),
        new(Pre, Sales, PreHead, "售前部"),
        new(East, Pre, EastHead, "华东组"),
        new(South, Pre, null, "华南组"),
        new(Lone, null, null, "综合部"),
    ];

    [Fact]
    public void Approver_is_the_nearest_head_up_the_tree()
    {
        Assert.Equal(East, ApproverDeptFor(Tree, East, Staff)!.Id);
        Assert.Equal(Pre, ApproverDeptFor(Tree, South, Staff)!.Id);   // 华南组没主管 → 售前部主管
        Assert.Null(ApproverDeptFor(Tree, Lone, Staff));               // 一路没主管 → 交系统管理员
        Assert.Null(ApproverDeptFor(Tree, null, Staff));               // 没分部门
    }

    [Fact]
    public void A_heads_own_items_go_to_the_head_above()
    {
        Assert.True(IsHead(Tree, EastHead));
        Assert.Equal(Pre, ApproverDeptFor(Tree, East, EastHead)!.Id);
    }

    [Fact]
    public void Scope_covers_headed_departments_and_everything_below()
    {
        Assert.Equal(new HashSet<Guid> { Pre, East, South }, ScopeOf(Tree, PreHead));
        Assert.Equal(new HashSet<Guid> { East }, ScopeOf(Tree, EastHead));
        Assert.Empty(ScopeOf(Tree, Staff));
    }

    [Fact]
    public void Detects_cycles_when_moving_departments()
    {
        Assert.True(WouldCycle(Tree, Pre, East));   // 售前部挂到自己的下级华东组下
        Assert.True(WouldCycle(Tree, Pre, Pre));
        Assert.False(WouldCycle(Tree, South, Lone));
        Assert.False(WouldCycle(Tree, East, null));
    }

    [Fact]
    public void Finds_similar_customer_names()
    {
        var names = new[] { "某新材料有限公司", "华东理工大学", "滨化集团" };
        Assert.Contains("某新材料有限公司", SimilarNames("某新材料科技有限公司", names));
        Assert.Contains("滨化集团", SimilarNames("滨化股份有限公司", names));
        Assert.Empty(SimilarNames("扬子石化", names));
    }
}
