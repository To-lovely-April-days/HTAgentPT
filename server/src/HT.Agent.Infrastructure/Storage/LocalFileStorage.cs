using HT.Agent.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace HT.Agent.Infrastructure.Storage;

public class StorageOptions
{
    /// <summary>本地实现的根目录。生产为 S3 兼容对象存储（表 8-2），接口不变，换实现不改业务代码。</summary>
    public string Root { get; set; } = "data/files";
}

/// <summary>本地文件存储。键形如 2025/09/guid_原名，避免同名覆盖。</summary>
public class LocalFileStorage(IOptions<StorageOptions> options) : IFileStorage
{
    private readonly string _root = Path.GetFullPath(options.Value.Root);

    public async Task<string> SaveAsync(Stream content, string keyHint, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var safe = string.Join("_", keyHint.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        var key = $"{now:yyyy}/{now:MM}/{Guid.NewGuid():N}_{safe}";
        var path = FullPath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var fs = File.Create(path);
        await content.CopyToAsync(fs, ct);
        return key;
    }

    public Task<Stream> OpenAsync(string key, CancellationToken ct = default)
        => Task.FromResult<Stream>(File.OpenRead(FullPath(key)));

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        var path = FullPath(key);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default)
        => Task.FromResult(File.Exists(FullPath(key)));

    private string FullPath(string key)
    {
        var full = Path.GetFullPath(Path.Combine(_root, key));
        if (!full.StartsWith(_root, StringComparison.Ordinal))
            throw new InvalidOperationException("非法存储键");
        return full;
    }
}
