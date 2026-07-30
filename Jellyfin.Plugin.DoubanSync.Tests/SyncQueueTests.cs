using Jellyfin.Plugin.DoubanSync.Services;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.DoubanSync.Tests;

public sealed class SyncQueueTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "douban-sync-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Queue_PersistsAndDeduplicatesSuccessfulItem()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var paths = new TestApplicationPaths(_temporaryDirectory);
        var job = new SyncJob
        {
            UserId = Guid.NewGuid(),
            ItemId = Guid.NewGuid(),
            Name = "测试电影",
            ProviderIds = new Dictionary<string, string>
            {
                ["DoubanID"] = "1292052"
            }
        };

        using (var first = new SyncQueue(paths, NullLogger<SyncQueue>.Instance))
        {
            await first.EnqueueAsync(job, CancellationToken.None);
            Assert.Equal(1, (await first.GetSnapshotAsync(CancellationToken.None)).PendingCount);
        }

        using (var second = new SyncQueue(paths, NullLogger<SyncQueue>.Instance))
        {
            var persisted = await second.GetDueJobAsync(CancellationToken.None);
            Assert.NotNull(persisted);
            await second.MarkSucceededAsync(persisted, "1292052", CancellationToken.None);
            await second.EnqueueAsync(
                new SyncJob
                {
                    UserId = job.UserId,
                    ItemId = Guid.NewGuid(),
                    Name = job.Name,
                    ProviderIds = new Dictionary<string, string>
                    {
                        ["DoubanID"] = "1292052"
                    }
                },
                CancellationToken.None);

            var snapshot = await second.GetSnapshotAsync(CancellationToken.None);
            Assert.Equal(0, snapshot.PendingCount);
            Assert.Equal("success", Assert.Single(snapshot.Recent).Status);
        }
    }

    [Fact]
    public async Task Queue_CompletionRemainsDeduplicatedAfterRecentHistoryRollsOff()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var paths = new TestApplicationPaths(_temporaryDirectory);
        var userId = Guid.NewGuid();
        var firstItem = new SyncJob
        {
            UserId = userId,
            ItemId = Guid.NewGuid(),
            Name = "最早完成的电影",
            ProviderIds = new Dictionary<string, string>
            {
                ["DoubanID"] = "1292052"
            }
        };

        using (var queue = new SyncQueue(paths, NullLogger<SyncQueue>.Instance))
        {
            await queue.EnqueueAsync(firstItem, CancellationToken.None);
            await queue.MarkSucceededAsync(firstItem, "1292052", CancellationToken.None);

            for (var index = 0; index < 100; index++)
            {
                var job = new SyncJob
                {
                    UserId = userId,
                    ItemId = Guid.NewGuid(),
                    Name = $"电影 {index}",
                    ProviderIds = new Dictionary<string, string>
                    {
                        ["DoubanID"] = (2000000 + index).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    }
                };
                await queue.EnqueueAsync(job, CancellationToken.None);
                await queue.MarkSucceededAsync(
                    job,
                    job.ProviderIds["DoubanID"],
                    CancellationToken.None);
            }

            Assert.DoesNotContain(
                (await queue.GetSnapshotAsync(CancellationToken.None)).Recent,
                record => record.DoubanId == "1292052");
        }

        using (var reloaded = new SyncQueue(paths, NullLogger<SyncQueue>.Instance))
        {
            await reloaded.EnqueueAsync(
                new SyncJob
                {
                    UserId = userId,
                    ItemId = Guid.NewGuid(),
                    Name = "最早完成的电影（另一版本）",
                    ProviderIds = new Dictionary<string, string>
                    {
                        ["DoubanID"] = "1292052"
                    }
                },
                CancellationToken.None);

            Assert.Equal(0, (await reloaded.GetSnapshotAsync(CancellationToken.None)).PendingCount);
        }
    }

    [Fact]
    public async Task Queue_PermanentFailureIsSuppressedUntilManualRetry()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var paths = new TestApplicationPaths(_temporaryDirectory);
        var job = new SyncJob
        {
            UserId = Guid.NewGuid(),
            ItemId = Guid.NewGuid(),
            Name = "无法匹配的电影"
        };

        using var queue = new SyncQueue(paths, NullLogger<SyncQueue>.Instance);
        await queue.EnqueueAsync(job, CancellationToken.None);
        await queue.MarkFailedAsync(job, "无法唯一匹配", false, CancellationToken.None);
        await queue.EnqueueAsync(
            new SyncJob
            {
                UserId = job.UserId,
                ItemId = job.ItemId,
                Name = job.Name
            },
            CancellationToken.None);

        Assert.Equal(0, (await queue.GetSnapshotAsync(CancellationToken.None)).PendingCount);

        await queue.RequeueFailedAsync(job.UserId, CancellationToken.None);

        Assert.Equal(1, (await queue.GetSnapshotAsync(CancellationToken.None)).PendingCount);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, true);
        }
    }

    private sealed class TestApplicationPaths : IApplicationPaths
    {
        public TestApplicationPaths(string root)
        {
            ProgramDataPath = root;
            WebPath = root;
            ProgramSystemPath = root;
            DataPath = root;
            ImageCachePath = root;
            PluginsPath = root;
            PluginConfigurationsPath = root;
            LogDirectoryPath = root;
            ConfigurationDirectoryPath = root;
            SystemConfigurationFilePath = Path.Combine(root, "system.xml");
            CachePath = root;
            TempDirectory = root;
            VirtualDataPath = root;
            TrickplayPath = root;
            BackupPath = root;
        }

        public string ProgramDataPath { get; }

        public string WebPath { get; }

        public string ProgramSystemPath { get; }

        public string DataPath { get; }

        public string ImageCachePath { get; }

        public string PluginsPath { get; }

        public string PluginConfigurationsPath { get; }

        public string LogDirectoryPath { get; }

        public string ConfigurationDirectoryPath { get; }

        public string SystemConfigurationFilePath { get; }

        public string CachePath { get; }

        public string TempDirectory { get; }

        public string VirtualDataPath { get; }

        public string TrickplayPath { get; }

        public string BackupPath { get; }

        public void MakeSanityCheckOrThrow()
        {
        }

        public void CreateAndCheckMarker(string path, string markerName, bool recursive = false)
        {
        }
    }
}
