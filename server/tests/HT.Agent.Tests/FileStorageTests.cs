using System.Text;
using HT.Agent.Infrastructure.Storage;
using Microsoft.Extensions.Options;

namespace HT.Agent.Tests;

public class FileStorageTests
{
    [Fact]
    public async Task 存到指定的键_按同一个键取得到_再存就覆盖()
    {
        var root = Path.Combine(Path.GetTempPath(), "ht-storage-" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new LocalFileStorage(Options.Create(new StorageOptions { Root = root }));
            var key = $"ppt-templates/{Guid.NewGuid():N}/p1.jpg";

            await storage.SaveAtAsync(key, new MemoryStream(Encoding.UTF8.GetBytes("旧图")));
            await storage.SaveAtAsync(key, new MemoryStream(Encoding.UTF8.GetBytes("新图")));

            Assert.True(await storage.ExistsAsync(key));
            using var reader = new StreamReader(await storage.OpenAsync(key));
            Assert.Equal("新图", await reader.ReadToEndAsync());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task 指定的键跑出存储目录_拒绝()
    {
        var root = Path.Combine(Path.GetTempPath(), "ht-storage-" + Guid.NewGuid().ToString("N"));
        var storage = new LocalFileStorage(Options.Create(new StorageOptions { Root = root }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.SaveAtAsync("../outside.jpg", new MemoryStream([1])));
    }
}
