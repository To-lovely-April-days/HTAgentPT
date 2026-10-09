using HT.Agent.Application.Abstractions;
using HT.Agent.Application.Logic;
using HT.Agent.Domain;
using HT.Agent.Infrastructure.Services;

namespace HT.Agent.Tests;

public sealed class ProjectResearchAgentTests
{
    [Fact]
    public async Task 规划器保留地点候选且不映射为客户()
    {
        var planner = new ProjectQueryPlanner(new FakeChat("""
            ```json
            {"customer":null,"deviceType":"反应釜","yearFrom":2024,"yearTo":null,"keyword":null,"locationHint":"上海","deliveryStatus":null,"amountMin":null,"amountMax":null,"allowRecent":false,"unresolvedConditions":[],"retrievalQuery":"上海 反应釜 项目"}
            ```
            """));

        var plan = await planner.PlanAsync("上海有哪些反应釜项目？", "");

        Assert.NotNull(plan.Query);
        Assert.Null(plan.Query!.CustomerName);
        Assert.Equal("上海", plan.Query.LocationHint);
        Assert.Equal("反应釜", plan.Query.DeviceType);
    }

    [Fact]
    public async Task 规划失败不能查询整张台账()
    {
        var planner = new ProjectQueryPlanner(new FakeChat("不是 JSON"));
        var plan = await planner.PlanAsync("有哪些项目？", "");
        Assert.Null(plan.Query);
        Assert.NotEmpty(plan.UnresolvedConditions);
    }

    [Fact]
    public async Task 无金额权限时不执行金额筛选()
    {
        var service = new FakeProjectService();
        var me = new FakeUser(new HashSet<Classification>());
        var agent = new ProjectResearchAgent(
            new ProjectQueryPlanner(new FakeChat("{}")), service, me);

        var result = await agent.ExecuteAsync(new ProjectQueryPlan(
            new ProjectSearchRequest(AmountMin: 100m), "金额项目", []));

        Assert.Null(result.Table);
        Assert.Equal(0, service.SearchCalls);
        Assert.Contains("金额", result.Detail);
    }

    [Fact]
    public async Task 地点候选标记且最多返回二十条()
    {
        var service = new FakeProjectService(Enumerable.Range(1, 21)
            .Select(i => new ProjectRow($"P-{i:000}", "客户", 2026, "反应釜", null, "上海现场", null,
                null, null, null, DateTimeOffset.UtcNow)).ToList());
        var me = new FakeUser(new HashSet<Classification> { Classification.Confidential });
        var agent = new ProjectResearchAgent(new ProjectQueryPlanner(new FakeChat("{}")), service, me);

        var result = await agent.ExecuteAsync(new ProjectQueryPlan(
            new ProjectSearchRequest(LocationHint: "上海"), "上海项目", []));

        Assert.NotNull(result.Table);
        Assert.Equal(20, result.Table!.Rows.Count);
        Assert.True(result.Table.Truncated);
        Assert.True(result.Table.IsCandidate);
        Assert.Contains("未核实", result.Table.Note);
    }

    private sealed class FakeChat(string response) : IChatModelClient
    {
        public Task<string> CompleteAsync(IReadOnlyList<ChatTurn> messages, CancellationToken ct = default)
            => Task.FromResult(response);

        public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatTurn> messages,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return await CompleteAsync(messages, ct);
        }
    }

    private sealed class FakeProjectService(IReadOnlyList<ProjectRow>? rows = null) : IProjectService
    {
        private readonly IReadOnlyList<ProjectRow> _rows = rows ?? [];
        public int SearchCalls { get; private set; }
        public Task<ProjectSearchResult> SearchAsync(ProjectSearchRequest req, CancellationToken ct = default)
        {
            SearchCalls++;
            return Task.FromResult(new ProjectSearchResult(_rows, true, false));
        }
        public Task<ProjectDetail?> GetAsync(string projectNo, CancellationToken ct = default)
            => Task.FromResult<ProjectDetail?>(null);
        public Task CreateAsync(ProjectEdit edit, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateAsync(string projectNo, ProjectEdit edit, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeUser(IReadOnlySet<Classification> classifications) : ICurrentUser
    {
        public Guid UserId => Guid.Empty;
        public string Username => "test";
        public string RoleCode => RoleCodes.PreSales;
        public Guid CompanyId => Guid.Empty;
        public Guid SessionId => Guid.Empty;
        public IReadOnlySet<Classification> Classifications => classifications;
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string> { PermissionKeys.ProjectSearch };
        public string? CustomerNo => null;
        public string? Ip => null;
        public string? TerminalId => null;
        public bool IsAuthenticated => true;
    }
}
