using System.Net;
using System.Text;
using HT.Agent.Application.Logic;
using HT.Agent.Infrastructure.Clients;

namespace HT.Agent.Tests;

public class MemoryAndLiteratureTests
{
    [Theory]
    [InlineData("报价一律按含税价写", null)]
    [InlineData("负责华东区的加氢项目", null)]
    [InlineData("设计压力习惯按 1.1 倍操作压力取", null)]
    [InlineData("张工手机 13812345678", "手机号")]
    [InlineData("发给 zhang@example.com", "邮箱")]
    [InlineData("身份证 11010119900307123X", "身份证号")]
    [InlineData("卡号 6222021234567890123", "银行卡号")]
    [InlineData("系统密码：abc123", "口令")]
    [InlineData("座机 021-61234567", "电话号码")]
    public void Memory_guard_keeps_preferences_and_rejects_sensitive(string text, string? why)
    {
        var r = MemoryGuard.Reject(text);
        if (why is null) Assert.Null(r);
        else Assert.Contains(why, r);
    }

    [Fact]
    public void Memory_guard_limits_length_and_normalizes_kind()
    {
        Assert.NotNull(MemoryGuard.Reject("好"));
        Assert.NotNull(MemoryGuard.Reject(new string('字', 201)));
        Assert.Equal("客户", MemoryGuard.NormalizeKind(" 客户 "));
        Assert.Equal("偏好", MemoryGuard.NormalizeKind("随便"));
        Assert.Equal("偏好", MemoryGuard.NormalizeKind(null));
    }

    [Fact]
    public async Task Papers_rebuild_abstract_from_inverted_index()
    {
        var json = """
            {"results":[{"id":"https://openalex.org/W1","doi":"https://doi.org/10.1/x","display_name":"Continuous polycondensation of nylon 66",
              "publication_year":2019,"cited_by_count":12,"primary_location":{"source":{"display_name":"Chem Eng J"}},
              "abstract_inverted_index":{"Nylon":[0],"66":[1],"is":[2],"made":[3],"continuously":[4]}},
             {"id":"https://openalex.org/W2","display_name":"","publication_year":null}]}
            """;
        var client = new LiteratureClient(new FakeFactory(_ => (HttpStatusCode.OK, json)));
        var items = await client.PapersAsync("nylon 66", 5, null, default);
        var p = Assert.Single(items);
        Assert.Equal("论文", p.Kind);
        Assert.Equal("Nylon 66 is made continuously", p.Abstract);
        Assert.Equal("Chem Eng J · 2019 · 被引 12", p.Meta);
        Assert.Equal("https://doi.org/10.1/x", p.Url);
    }

    [Fact]
    public async Task Patents_parse_single_and_list_shapes_and_prefer_chinese_title()
    {
        var search = """
            {"ops:world-patent-data":{"ops:biblio-search":{"ops:search-result":{"exchange-documents":[
              {"exchange-document":{"@country":"CN","@doc-number":"112345678","@kind":"A",
                "bibliographic-data":{
                  "invention-title":[{"@lang":"en","$":"Hydrogenation reactor"},{"@lang":"zh","$":"一种加氢反应釜"}],
                  "publication-reference":{"document-id":[{"@document-id-type":"docdb","date":{"$":"20210305"}}]},
                  "parties":{"applicants":{"applicant":[{"applicant-name":{"name":{"$":"某公司"}}}]}}},
                "abstract":{"@lang":"en","p":{"$":"A reactor with a stirrer."}}}},
              {"exchange-document":{"@country":"US","@doc-number":"9999999","@kind":"B2",
                "bibliographic-data":{"invention-title":{"@lang":"en","$":"Stirred vessel"}}}}
            ]}}}}
            """;
        var client = new LiteratureClient(new FakeFactory(req => req.RequestUri!.AbsolutePath.EndsWith("accesstoken")
            ? (HttpStatusCode.OK, """{"access_token":"t","expires_in":"1199"}""")
            : (HttpStatusCode.OK, search)));
        var items = await client.PatentsAsync("hydrogenation reactor", 5, "k", "s", default);
        Assert.Equal(2, items.Count);
        Assert.Equal("一种加氢反应釜", items[0].Title);
        Assert.Contains("CN112345678A", items[0].Meta);
        Assert.Contains("2021", items[0].Meta);
        Assert.Contains("某公司", items[0].Meta);
        Assert.Equal("A reactor with a stirrer.", items[0].Abstract);
        Assert.Contains("pn%3DCN112345678A", items[0].Url);
        Assert.Equal("Stirred vessel", items[1].Title);
    }

    [Fact]
    public async Task Patents_no_result_returns_empty()
    {
        var client = new LiteratureClient(new FakeFactory(req => req.RequestUri!.AbsolutePath.EndsWith("accesstoken")
            ? (HttpStatusCode.OK, """{"access_token":"t","expires_in":1199}""")
            : (HttpStatusCode.NotFound, "<fault/>")));
        Assert.Empty(await client.PatentsAsync("zzzz", 5, "k2", "s2", default));
    }

    private sealed class FakeFactory(Func<HttpRequestMessage, (HttpStatusCode, string)> reply) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(reply));

        private sealed class Handler(Func<HttpRequestMessage, (HttpStatusCode, string)> reply) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var (code, body) = reply(request);
                return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
            }
        }
    }
}
