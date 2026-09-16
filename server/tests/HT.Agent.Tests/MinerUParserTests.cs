using System.Text.Json;
using HT.Agent.Application.Abstractions;
using HT.Agent.Infrastructure.Clients;

namespace HT.Agent.Tests;

public class MinerUParserTests
{
    private static JsonElement Root(string json)
        => JsonDocument.Parse(json).RootElement;

    private static string Wrap(string contentListJson)
        => """{"results":{"doc":{"md_content":null,"content_list":""" + contentListJson + "}}}";

    [Fact]
    public void 标题_段落_页码映射()
    {
        var blocks = MinerUParserClient.MapResponse(Root(Wrap("""
            [
              {"type":"text","text":"CJF 系列安装手册","text_level":1,"page_idx":0},
              {"type":"text","text":"安装前请确认电源电压与铭牌一致。","page_idx":0},
              {"type":"text","text":"二、轴承复装","text_level":2,"page_idx":3}
            ]
            """)));
        Assert.Equal(3, blocks.Count);
        Assert.Equal(new ParsedBlock("heading", "CJF 系列安装手册", 1, 1, null, null), blocks[0]);
        Assert.Equal("paragraph", blocks[1].Kind);
        Assert.Null(blocks[1].Level);
        Assert.Equal(1, blocks[1].PageNo);          // page_idx 0 起 → 页码 1 起
        Assert.Equal(2, blocks[2].Level);
        Assert.Equal(4, blocks[2].PageNo);
    }

    [Fact]
    public void 表格_首行为表头_数据行携带表头上下文()
    {
        var blocks = MinerUParserClient.MapResponse(Root(Wrap("""
            [{"type":"table",
              "table_caption":["表1 力矩要求"],
              "table_body":"<html><body><table><tr><th>部件</th><th>力矩</th></tr><tr><td>轴承压盖</td><td>12 N·m</td></tr><tr><td>联轴器</td><td>25 N·m</td></tr></table></body></html>",
              "table_footnote":["注：常温工况"],
              "page_idx":1}]
            """)));
        Assert.Equal(4, blocks.Count);
        Assert.Equal(("paragraph", "表1 力矩要求"), (blocks[0].Kind, blocks[0].Text));
        Assert.Equal("table_row", blocks[1].Kind);
        Assert.Equal("轴承压盖 | 12 N·m", blocks[1].Text);
        Assert.Equal("部件 | 力矩", blocks[1].TableHeader);
        Assert.Equal("联轴器 | 25 N·m", blocks[2].Text);
        Assert.Equal("部件 | 力矩", blocks[2].TableHeader);
        Assert.Equal(("paragraph", "注：常温工况"), (blocks[3].Kind, blocks[3].Text));
        Assert.All(blocks, b => Assert.Equal(2, b.PageNo));
    }

    [Fact]
    public void 表格识别失败只有截图时_保留题注不产生空行()
    {
        var blocks = MinerUParserClient.MapResponse(Root(Wrap("""
            [{"type":"table","table_caption":["表2 备件清单"],"img_path":"images/x.jpg","page_idx":0}]
            """)));
        Assert.Single(blocks);
        Assert.Equal("表2 备件清单", blocks[0].Text);
    }

    [Fact]
    public void 列表合并为一段_图片保留题注_公式与代码保留文本()
    {
        var blocks = MinerUParserClient.MapResponse(Root(Wrap("""
            [
              {"type":"list","list_items":["断电","泄压","拆护罩"],"page_idx":1},
              {"type":"image","img_path":"images/a.jpg","image_caption":["图2 接线示意"],"page_idx":2},
              {"type":"equation","text":"$$P=UI$$","text_format":"latex","page_idx":2},
              {"type":"code","code_body":"E-17: OVERLOAD","code_caption":["报警代码"],"page_idx":3},
              {"type":"image","img_path":"images/b.jpg","image_caption":[],"page_idx":3}
            ]
            """)));
        Assert.Equal(4, blocks.Count); // 无题注的图片不产块
        Assert.Equal("断电\n泄压\n拆护罩", blocks[0].Text);
        Assert.Equal("图2 接线示意", blocks[1].Text);
        Assert.Equal("$$P=UI$$", blocks[2].Text);
        Assert.Equal("报警代码\nE-17: OVERLOAD", blocks[3].Text);
        Assert.All(blocks, b => Assert.Equal("paragraph", b.Kind));
    }

    [Fact]
    public void content_list_为JSON字符串时同样可解()
    {
        // 部分版本把 content_list 以字符串形式内嵌
        var wrapped = """{"results":{"doc":{"content_list":"[{\"type\":\"text\",\"text\":\"正文\",\"page_idx\":0}]"}}}""";
        var blocks = MinerUParserClient.MapResponse(Root(wrapped));
        Assert.Single(blocks);
        Assert.Equal("正文", blocks[0].Text);
    }

    [Fact]
    public void bbox_数组连成字符串保留位置()
    {
        var blocks = MinerUParserClient.MapResponse(Root(Wrap("""
            [{"type":"text","text":"定位段","bbox":[52,109.5,286,183],"page_idx":0}]
            """)));
        Assert.Equal("52,109.5,286,183", blocks[0].Bbox);
    }

    [Fact]
    public void 缺少results或content_list_报内容性错误而非崩溃()
    {
        Assert.Throws<ParseContentException>(() => MinerUParserClient.MapResponse(Root("""{"detail":"Not Found"}""")));
        Assert.Throws<ParseContentException>(() => MinerUParserClient.MapResponse(Root("""{"results":{}}""")));
        Assert.Throws<ParseContentException>(() => MinerUParserClient.MapResponse(Root("""{"results":{"doc":{"md_content":"# x"}}}""")));
    }

    [Fact]
    public void 本地引擎_images字典按文件名尾段匹配_dataURI解出字节()
    {
        var b64 = Convert.ToBase64String("IMGDATA"u8.ToArray());
        var wrapped = Root(
            "{\"results\":{\"doc\":{\"content_list\":[{\"type\":\"text\",\"text\":\"正文\",\"page_idx\":0}," +
            "{\"type\":\"image\",\"img_path\":\"images/pic1.jpg\",\"image_caption\":[\"图1\"],\"page_idx\":1}]," +
            "\"images\":{\"pic1.jpg\":\"data:image/jpeg;base64," + b64 + "\"}}}}");
        var (blocks, images) = MinerUParserClient.MapResponseFull(wrapped);
        Assert.Equal(2, blocks.Count); // 正文 + 题注段（题注文字始终进正文流参与检索）
        Assert.Single(images);
        Assert.Equal("IMGDATA"u8.ToArray(), images[0].Bytes);
        Assert.Equal(("图1", 2), (images[0].Caption, images[0].PageNo));
    }

    [Fact]
    public void 文件名压成ASCII_扩展名保留()
    {
        Assert.Equal("manual.pdf", MinerUParserClient.SafeAsciiFileName("manual.pdf"));
        Assert.Equal("____.pdf", MinerUParserClient.SafeAsciiFileName("维护手册.pdf"));
        Assert.Equal("CJF-5L__.docx", MinerUParserClient.SafeAsciiFileName("CJF-5L方案.docx"));
        Assert.Equal("a_b_.pdf", MinerUParserClient.SafeAsciiFileName("a\"b\\.pdf"));
    }

    [Fact]
    public void 表格HTML拆行_去标签还原实体压缩空白()
    {
        var rows = MinerUParserClient.HtmlTableRows(
            "<table><tr><td colspan=\"2\"><b>A&amp;B</b>  值</td></tr><tr><td> x </td><td>\n y\n</td></tr><tr><td></td></tr></table>");
        Assert.Equal(2, rows.Count); // 全空行被丢弃
        Assert.Equal(["A&B 值"], rows[0]);
        Assert.Equal(["x", "y"], rows[1]);
    }
}
