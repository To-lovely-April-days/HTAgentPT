using System.Diagnostics;

namespace HT.Agent.Infrastructure.Clients;

/// <summary>EMF/WMF 矢量图转 PNG。浏览器和看图模型都不认这两种格式，而 Visio、CAD 粘进 Word 的
/// PID 图、装配图偏偏多是它们。转换靠服务器上现成的工具：先找 Inkscape（转得最准），再找 LibreOffice；
/// 都没装就返回 null，调用方照旧跳过这张图——不装工具，行为和以前一样。
/// 单张最多等 30 秒，转不出来不影响整篇解析。</summary>
public static class VectorImageConverter
{
    private static readonly string[] VectorTypes =
        ["image/x-emf", "image/emf", "image/x-wmf", "image/wmf", "application/x-msmetafile"];

    private static readonly Lazy<(string Exe, bool Inkscape)?> Tool = new(FindTool);

    public static bool IsVector(string contentType)
        => VectorTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase);

    public static bool Available => Tool.Value is not null;

    public static byte[]? ToPng(byte[] bytes, string contentType)
    {
        if (Tool.Value is not { } tool) return null;
        var ext = contentType.Contains("wmf", StringComparison.OrdinalIgnoreCase) ? ".wmf" : ".emf";
        var dir = Directory.CreateTempSubdirectory("htvec-").FullName;
        try
        {
            var input = Path.Combine(dir, "in" + ext);
            File.WriteAllBytes(input, bytes);
            var output = Path.Combine(dir, "in.png");
            var psi = tool.Inkscape
                ? new ProcessStartInfo(tool.Exe) { ArgumentList = { input, "--export-type=png", $"--export-filename={output}", "--export-dpi=150" } }
                : new ProcessStartInfo(tool.Exe) { ArgumentList = { "--headless", "--convert-to", "png", "--outdir", dir, input } };
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            using var p = Process.Start(psi);
            if (p is null) return null;
            if (!p.WaitForExit(30_000))
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return null;
            }
            return File.Exists(output) && new FileInfo(output).Length > 0 ? File.ReadAllBytes(output) : null;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private static (string, bool)? FindTool()
    {
        foreach (var (name, inkscape) in new[] { ("inkscape", true), ("soffice", false), ("libreoffice", false) })
            if (OnPath(name) is { } exe) return (exe, inkscape);
        return null;
    }

    private static string? OnPath(string name)
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var names = OperatingSystem.IsWindows() ? new[] { name + ".exe", name + ".com" } : new[] { name };
        foreach (var dir in paths)
            foreach (var n in names)
            {
                var full = Path.Combine(dir, n);
                if (File.Exists(full)) return full;
            }
        return null;
    }
}
