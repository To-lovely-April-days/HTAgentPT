using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace HT.Agent.Application.Logic;

/// <summary>图片理解的纯逻辑：送不送去看（尺寸、去重、上限）、看图的说法、模型回话怎么收。
/// 知识库解析与对话附件解析共用——同一张 PID 图不管从哪条路进来，变成的文字一个样。
///
/// 模型看完写成一段「图片描述」，作为一个分块入库：能检索、能被引用；回答里注明出自图片描述（模型生成），
/// 原图在来源里给人对照。看不懂的图（标志、装饰、页眉）模型回「无关」，就不入库。</summary>
public static class ImageNotes
{
    /// <summary>一张待看的图。Page 为页码（PDF 有，Word 没有）。</summary>
    public record Candidate(byte[] Bytes, string ContentType, string? Caption, int? Page, int? Index = null);

    /// <summary>看完的结果：Kind 是模型判的类型（PID流程图/设备照片…），Text 是要入库的整段文字。</summary>
    public record Note(int? Page, string? Caption, string Kind, string Text, int? Index = null);

    /// <summary>看图的说法：先判类型；PID / 工艺流程图按固定格式列位号与流向（这是工程师真会问的），
    /// 其他图写一两段描述。看不清不许猜——宁可写「看不清」，也不编一个位号出来。</summary>
    public const string Prompt = """
        你在为化工实验设备企业整理技术资料里的图片。先判断图片类型，再严格按格式输出，不要输出其他内容。
        类型只能是：PID流程图、工艺流程图、设备图纸、设备照片、表格、示意图、无关。
        标志、装饰、空白、页眉页脚、二维码一律判为「无关」，只输出一行：类型：无关

        如果是 PID流程图 或 工艺流程图，输出：
        类型：PID流程图
        设备：逐个列出 位号 名称（如 R-101 反应釜；E-101 冷凝器）
        仪表与阀门：逐个列出位号（如 PT-101、TT-101、PV-101、PSV-101）
        主要流向：用 → 写物料从哪台设备到哪台（如 R-101 → E-101 → V-201）
        说明：图上其他看得清的文字（介质、管径、控制回路、公用工程）

        其他类型输出：
        类型：（上面之一）
        描述：一两段话写清图里有什么，以及图上能看清的文字和数字（型号、参数、尺寸）。

        看不清的地方写「看不清」，不要猜位号和数字。
        """;

    /// <summary>收模型的回话：判成「无关」或空回话不要；去掉思考标签与代码围栏；限长。</summary>
    public static Note? Parse(string reply, int? page, string? caption, int? index = null)
    {
        if (string.IsNullOrWhiteSpace(reply)) return null;
        var text = Regex.Replace(reply, @"<think>[\s\S]*?</think>", "").Trim();
        text = Regex.Replace(text, @"^```\w*\s*|\s*```$", "").Trim();
        var kind = Regex.Match(text, @"类型\s*[：:]\s*([^\n]+)") is { Success: true } m ? m.Groups[1].Value.Trim() : "图片";
        if (kind.Contains("无关") || text.Length < 6) return null;
        if (text.Length > 2400) text = text[..2400] + "…";
        return new Note(page, caption, kind, text, index);
    }

    /// <summary>入库的分块文字：开头一行标明这是模型看图写的、在第几页，回答引用时就说得清出处与分量。</summary>
    public static string ChunkText(Note n)
    {
        var where = string.Join(" · ", new[] { n.Page is { } p ? $"第 {p} 页" : null, n.Caption }.Where(s => !string.IsNullOrWhiteSpace(s)));
        // 「〔图3〕」：第几张图，分块视图据此把原图配在这段描述旁边
        var mark = n.Index is { } i ? $"〔图{i}〕" : "";
        return $"【图片描述（模型生成，以原图为准）{(where.Length > 0 ? " · " + where : "")}】{mark}\n{n.Text}";
    }

    public const string SectionName = "图片描述";

    /// <summary>挑要送去看的图：太小的（图标、项目符号，短边 &lt; 120 像素或文件 &lt; 4KB）不看，
    /// 同一张图出现多次（每页页眉的标志）只看一次，最多 max 张——一份手册几百张图全送去看，解析要排到明天。
    /// 大图优先：PID、图纸往往是文档里最大的那几张。</summary>
    public static List<Candidate> Select(IEnumerable<Candidate> images, int max)
    {
        var seen = new HashSet<string>();
        var keep = new List<(Candidate C, int Order)>();
        var order = 0;
        foreach (var c in images)
        {
            order++;
            if (c.Bytes.Length < 4 * 1024) continue;
            if (Dimensions(c.Bytes) is { } d && Math.Min(d.W, d.H) < 120) continue;
            if (!seen.Add(Convert.ToHexString(SHA256.HashData(c.Bytes)))) continue;
            keep.Add((c, order));
        }
        return keep.OrderByDescending(x => x.C.Bytes.Length).Take(Math.Max(0, max))
            .OrderBy(x => x.Order).Select(x => x.C).ToList();
    }

    /// <summary>从文件头读宽高（PNG / JPEG / GIF / BMP），读不出返回 null（不因此丢图）。</summary>
    public static (int W, int H)? Dimensions(byte[] b)
    {
        if (b.Length >= 24 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47)
            return (BigEndian(b, 16), BigEndian(b, 20));
        if (b.Length >= 10 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F')
            return (b[6] | (b[7] << 8), b[8] | (b[9] << 8));
        if (b.Length >= 26 && b[0] == 'B' && b[1] == 'M')
            return (BitConverter.ToInt32(b, 18), Math.Abs(BitConverter.ToInt32(b, 22)));
        if (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xD8)
        {
            var i = 2;
            while (i + 9 < b.Length)
            {
                if (b[i] != 0xFF) { i++; continue; }
                var marker = b[i + 1];
                var len = (b[i + 2] << 8) | b[i + 3];
                // SOF0..SOF15（除 DHT C4、JPG C8、DAC CC）里放着尺寸
                if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                    return ((b[i + 7] << 8) | b[i + 8], (b[i + 5] << 8) | b[i + 6]);
                if (len < 2) break;
                i += 2 + len;
            }
        }
        return null;
    }

    private static int BigEndian(byte[] b, int at) => (b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3];
}
