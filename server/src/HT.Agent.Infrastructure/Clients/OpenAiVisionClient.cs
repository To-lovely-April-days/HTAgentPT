using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HT.Agent.Application.Abstractions;
using HT.Agent.Domain;

namespace HT.Agent.Infrastructure.Clients;

/// <summary>看图（OpenAI 兼容的图片输入）：消息里带 image_url（base64 data URL），vLLM、在线多模态接口都认这种写法。
/// 地址与模型名默认跟对话模型走（同一个 Qwen 既能对话也能看图时不用另配），也可以单独指向另一个看图模型。
/// 模型不收图（vLLM 带 --language-model-only 启动、或换成了纯文本模型）会回 400 —— 归为「看图不可用」，
/// 由调用方停掉这一批，解析照常完成。</summary>
public class OpenAiVisionClient(IHttpClientFactory httpFactory, IRuntimeConfig config) : IVisionClient
{
    public async Task<string> DescribeAsync(byte[] image, string contentType, string instruction, CancellationToken ct = default)
    {
        var chatUrl = await config.GetStringAsync(ConfigKeys.ChatModelUrl, "http://127.0.0.1:8000/v1", ct);
        var baseUrl = (await config.GetStringAsync(ConfigKeys.VisionUrl, "", ct)).Trim();
        if (baseUrl.Length == 0) baseUrl = chatUrl;
        var model = (await config.GetStringAsync(ConfigKeys.VisionModelName, "", ct)).Trim();
        if (model.Length == 0) model = await config.GetStringAsync(ConfigKeys.ChatModelName, "chat-default", ct);
        var apiKey = (await config.GetStringAsync(ConfigKeys.ChatApiKey, "", ct)).Trim();

        var body = BuildBody(model, image, contentType, instruction);
        var http = httpFactory.CreateClient("model");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/chat/completions")
        {
            Content = JsonContent.Create(body)
        };
        if (apiKey.Length > 0)
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

        HttpResponseMessage resp;
        try { resp = await http.SendAsync(req, ct); }
        catch (HttpRequestException ex) { throw new VisionUnavailableException($"看图接口连不上：{ex.Message}", ex); }
        using (resp)
        {
            if (resp.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity)
            {
                var detail = await resp.Content.ReadAsStringAsync(ct);
                throw new VisionUnavailableException(
                    $"模型不收图片（HTTP {(int)resp.StatusCode}）。若用 vLLM，启动时去掉 --language-model-only。{Trim(detail, 160)}");
            }
            resp.EnsureSuccessStatusCode();
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        }
    }

    /// <summary>请求体：system 放看图说法，user 放一句话 + 图片（data URL）。温度压低，只要照实描述。</summary>
    internal static object BuildBody(string model, byte[] image, string contentType, string instruction) => new
    {
        model,
        temperature = 0.1,
        max_tokens = 900,
        stream = false,
        messages = new object[]
        {
            new { role = "system", content = instruction },
            new
            {
                role = "user",
                content = new object[]
                {
                    new { type = "text", text = "请按要求整理这张图片。" },
                    new { type = "image_url", image_url = new { url = $"data:{contentType};base64,{Convert.ToBase64String(image)}" } }
                }
            }
        }
    };

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
