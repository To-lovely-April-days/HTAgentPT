using System.Text.Json;
using HT.Agent.Application.Abstractions;
using HT.Agent.Application.Logic;
using HT.Agent.Domain;
using HT.Agent.Infrastructure.Clients;
using HT.Agent.Infrastructure.Vision;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace HT.Agent.Tests;

/// <summary>图片理解：挑图、收模型回话、入库文字、请求格式、开关与「模型不收图」时的退路。</summary>
public class ImageUnderstandingTests
{
    /// <summary>造一张指定宽高的 PNG（只有文件头是真的，后面补零到指定体积——挑图只看头和体积）。</summary>
    private static byte[] Png(int w, int h, int size = 8192, byte salt = 0)
    {
        var b = new byte[size];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52 }.CopyTo(b, 0);
        b[16] = (byte)(w >> 24); b[17] = (byte)(w >> 16); b[18] = (byte)(w >> 8); b[19] = (byte)w;
        b[20] = (byte)(h >> 24); b[21] = (byte)(h >> 16); b[22] = (byte)(h >> 8); b[23] = (byte)h;
        b[^1] = salt;
        return b;
    }

    private static ImageNotes.Candidate C(byte[] bytes, int? page = null) => new(bytes, "image/png", null, page);

    [Fact]
    public void 挑图_小图不看_重复只看一次_超上限取大图且保持原顺序()
    {
        Assert.Equal((800, 600), ImageNotes.Dimensions(Png(800, 600)));
        var logo = Png(800, 600, salt: 1);
        var picked = ImageNotes.Select(
        [
            C(Png(40, 40)),                       // 图标
            C(Png(800, 600, size: 2000)),         // 体积太小
            C(logo, 1), C(logo, 2), C(logo, 3),   // 每页页眉同一张
            C(Png(1600, 1200, size: 60000, salt: 2), 4),
            C(Png(900, 700, size: 20000, salt: 3), 5),
        ], max: 2);
        Assert.Equal([4, 5], picked.Select(p => p.Page));
        Assert.Equal(3, ImageNotes.Select([C(logo, 1), C(logo, 2), C(Png(1600, 1200, size: 60000, salt: 2)), C(Png(900, 700, size: 20000, salt: 3))], 10).Count);
    }

    [Fact]
    public void 收回话_无关的不要_PID保留_去掉思考段()
    {
        Assert.Null(ImageNotes.Parse("类型：无关", 1, null));
        Assert.Null(ImageNotes.Parse("  ", 1, null));
        var pid = ImageNotes.Parse("<think>先看看</think>\n类型：PID流程图\n设备：R-101 反应釜；E-101 冷凝器\n主要流向：R-101 → E-101", 3, "图1 工艺流程");
        Assert.NotNull(pid);
        Assert.Equal("PID流程图", pid!.Kind);
        Assert.DoesNotContain("think", pid.Text);

        var text = ImageNotes.ChunkText(pid);
        Assert.Contains("〔图2〕", ImageNotes.ChunkText(pid with { Index = 2 }));
        Assert.DoesNotContain("〔图", text);
        Assert.StartsWith("【图片描述（模型生成，以原图为准） · 第 3 页 · 图1 工艺流程】", text);
        Assert.Contains("R-101 → E-101", text);
    }

    [Fact]
    public void 请求格式_OpenAI兼容的图片输入()
    {
        var body = OpenAiVisionClient.BuildBody("Qwen/Qwen3.8-27B", [1, 2, 3], "image/png", ImageNotes.Prompt);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(body));
        var msgs = doc.RootElement.GetProperty("messages");
        Assert.Equal("system", msgs[0].GetProperty("role").GetString());
        var parts = msgs[1].GetProperty("content");
        Assert.Equal("image_url", parts[1].GetProperty("type").GetString());
        Assert.Equal("data:image/png;base64,AQID", parts[1].GetProperty("image_url").GetProperty("url").GetString());
    }

    private sealed class FakeConfig(Dictionary<string, string> values) : IRuntimeConfig
    {
        public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult(values.GetValueOrDefault(key));
        public Task<int> GetIntAsync(string key, int fallback, CancellationToken ct = default)
            => Task.FromResult(values.TryGetValue(key, out var v) && int.TryParse(v, out var i) ? i : fallback);
        public Task<double> GetDoubleAsync(string key, double fallback, CancellationToken ct = default) => Task.FromResult(fallback);
        public Task<string> GetStringAsync(string key, string fallback, CancellationToken ct = default)
            => Task.FromResult(values.GetValueOrDefault(key) ?? fallback);
        public Task SetAsync(string key, string value, Guid? updatedBy, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, string>>(values);
    }

    private sealed class FakeVision(Func<int, string> reply) : IVisionClient
    {
        public int Calls;
        public Task<string> DescribeAsync(byte[] image, string contentType, string instruction, CancellationToken ct = default)
            => Task.FromResult(reply(++Calls));
    }

    private static ImageDescriber Describer(IVisionClient vision, bool enabled)
        => new(vision, new FakeConfig(new()
        {
            [ConfigKeys.VisionEnabled] = enabled ? "true" : "false",
            [ConfigKeys.ChatProvider] = "openai",
        }), new ConfigurationBuilder().Build(), NullLogger<ImageDescriber>.Instance);

    private static List<ParsedImage> Images(int n) => Enumerable.Range(1, n)
        .Select(i => new ParsedImage(Png(1000, 800, size: 9000 + i, salt: (byte)i), $"p{i}.png", "image/png", null, i, null)).ToList();

    [Fact]
    public async Task 开关关着_一张图都不送()
    {
        var vision = new FakeVision(_ => "类型：设备照片\n描述：反应釜");
        Assert.Empty(await Describer(vision, enabled: false).DescribeAsync(Images(3), "a.pdf", default));
        Assert.Equal(0, vision.Calls);
    }

    [Fact]
    public async Task 开关打开_逐张描述_无关的丢掉()
    {
        var vision = new FakeVision(i => i == 2 ? "类型：无关" : "类型：设备照片\n描述：带搅拌的反应釜");
        var notes = await Describer(vision, enabled: true).DescribeAsync(Images(3), "a.pdf", default);
        Assert.Equal(3, vision.Calls);
        Assert.Equal([1, 3], notes.Select(n => n.Page));
    }

    private sealed class RejectingVision : IVisionClient
    {
        public int Calls;
        public Task<string> DescribeAsync(byte[] image, string contentType, string instruction, CancellationToken ct = default)
        {
            Calls++;
            throw new VisionUnavailableException("模型不收图片");
        }
    }

    [Fact]
    public async Task 模型不收图_停掉这一批_不抛错()
    {
        var vision = new RejectingVision();
        var notes = await Describer(vision, enabled: true).DescribeAsync(Images(5), "a.pdf", default);
        Assert.Empty(notes);
        Assert.Equal(1, vision.Calls);   // 第一张就停，不再一张张撞
    }

    [Fact]
    public void 矢量图格式能认出来()
    {
        Assert.True(VectorImageConverter.IsVector("image/x-emf"));
        Assert.True(VectorImageConverter.IsVector("image/x-wmf"));
        Assert.False(VectorImageConverter.IsVector("image/png"));
    }
}
