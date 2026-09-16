using System.IO.Compression;
using System.Text;
using HT.Agent.Application.Abstractions;
using HT.Agent.Infrastructure.Clients;

namespace HT.Agent.Tests;

public class MinerUOnlineParserTests
{
    [Fact]
    public void 批次创建_取batchId与首个上传地址()
    {
        var (batchId, url) = MinerUOnlineParserClient.ParseBatchCreate(
            """{"code":0,"msg":"ok","data":{"batch_id":"b-123","file_urls":["https://oss.example/put1","https://oss.example/put2"]}}""");
        Assert.Equal("b-123", batchId);
        Assert.Equal("https://oss.example/put1", url);
    }

    [Fact]
    public void 批次创建_code非零带msg报出_不吞原因()
    {
        var ex = Assert.Throws<ParseContentException>(() => MinerUOnlineParserClient.ParseBatchCreate(
            """{"code":-60012,"msg":"token 已过期","data":null}"""));
        Assert.Contains("token 已过期", ex.Reason);
        Assert.Contains("-60012", ex.Reason);
    }

    [Fact]
    public void 批次状态_按文件名匹配条目()
    {
        var json = """
            {"code":0,"data":{"batch_id":"b1","extract_result":[
              {"file_name":"other.pdf","state":"running"},
              {"file_name":"mine.pdf","state":"done","full_zip_url":"https://oss.example/r.zip","err_msg":""}
            ]}}
            """;
        var (state, zip, _) = MinerUOnlineParserClient.ParseBatchState(json, "mine.pdf");
        Assert.Equal("done", state);
        Assert.Equal("https://oss.example/r.zip", zip);
    }

    [Fact]
    public void 批次状态_dataId优先于文件名_服务端改名也能对上()
    {
        // 服务端可能改写文件名——data_id 是我们自己生成的，匹配以它优先
        var json = """
            {"code":0,"data":{"batch_id":"b1","extract_result":[
              {"file_name":"renamed-by-server.pdf","data_id":"d-42","state":"done","full_zip_url":"https://oss.example/d42.zip"},
              {"file_name":"mine.pdf","data_id":"d-99","state":"failed","err_msg":"其他人的"}
            ]}}
            """;
        var (state, zip, _) = MinerUOnlineParserClient.ParseBatchState(json, "mine.pdf", "d-42");
        Assert.Equal("done", state);
        Assert.Equal("https://oss.example/d42.zip", zip);
    }

    [Fact]
    public void 批次状态_失败态带原因_结果未就绪按pending()
    {
        var (state, _, err) = MinerUOnlineParserClient.ParseBatchState(
            """{"data":{"extract_result":[{"file_name":"a.pdf","state":"failed","err_msg":"页数超限"}]}}""", "a.pdf");
        Assert.Equal("failed", state);
        Assert.Equal("页数超限", err);
        var (state2, _, _) = MinerUOnlineParserClient.ParseBatchState("""{"data":{}}""", "a.pdf");
        Assert.Equal("pending", state2);
    }

    private static byte[] MakeZip(params (string Name, string Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in entries)
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(Encoding.UTF8.GetBytes(content));
            }
        return ms.ToArray();
    }

    [Fact]
    public void 结果包_找到content_list并映射为块()
    {
        var zip = MakeZip(
            ("full.md", "# 手册"),
            ("cjf-manual_content_list.json",
             """[{"type":"text","text":"CJF 手册","text_level":1,"page_idx":0},{"type":"text","text":"正文段。","page_idx":1}]"""));
        var blocks = MinerUOnlineParserClient.ExtractBlocksFromZip(zip);
        Assert.Equal(2, blocks.Count);
        Assert.Equal(("heading", 1), (blocks[0].Kind, blocks[0].PageNo));
        Assert.Equal(("paragraph", 2), (blocks[1].Kind, blocks[1].PageNo));
    }

    [Fact]
    public void 结果包_缺content_list报内容性错误()
    {
        var zip = MakeZip(("full.md", "# 只有 markdown"));
        Assert.Throws<ParseContentException>(() => MinerUOnlineParserClient.ExtractBlocksFromZip(zip));
    }

    [Fact]
    public void 结果包_按img_path取图_含表格截图_缺图不挡正文()
    {
        var contentList = """
            [
              {"type":"text","text":"接线说明","text_level":1,"page_idx":0},
              {"type":"image","img_path":"images/w1.jpg","image_caption":["图2 接线示意"],"bbox":[10,20,300,200],"page_idx":2},
              {"type":"table","img_path":"images/t1.png","table_caption":["表3 力矩"],"table_body":"<table><tr><td>x</td></tr></table>","page_idx":3},
              {"type":"image","img_path":"images/missing.jpg","image_caption":["包里不存在的图"],"page_idx":4}
            ]
            """;
        var zip = MakeZip(
            ("doc/auto/doc_content_list.json", contentList),
            ("doc/auto/images/w1.jpg", "JPGBYTES"),
            ("doc/auto/images/t1.png", "PNGBYTES"));
        var (blocks, images) = MinerUOnlineParserClient.ExtractFromZip(zip);
        Assert.True(blocks.Count >= 2);
        Assert.Equal(2, images.Count); // 缺失的第三张跳过，不抛错
        Assert.Equal(("图2 接线示意", 3, "image/jpeg"), (images[0].Caption, images[0].PageNo, images[0].ContentType));
        Assert.Equal("10,20,300,200", images[0].Bbox);
        Assert.Equal("JPGBYTES"u8.ToArray(), images[0].Bytes);
        Assert.Equal(("表3 力矩", 4, "image/png"), (images[1].Caption, images[1].PageNo, images[1].ContentType));
    }
}
