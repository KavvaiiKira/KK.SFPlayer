using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using KK.SFPlayer.Services;
using Xunit;

namespace KK.SFPlayer.Tests
{
    public sealed class ArtworkCacheStoreTests
    {
        [Fact]
        public async Task ArtworkIsReusedFromMemoryAndDiskAndInvalidationDownloadsAgain()
        {
            var root = Path.Combine(Path.GetTempPath(), "KK.SFPlayer.Tests");
            var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
            try
            {
                using var handler = new ImageHandler();
                using var client = new HttpClient(handler);
                var uri = new Uri("https://example.com/artwork");
                var cache = new ArtworkCacheStore(directory);
                var bytes = await cache.LoadAsync(uri, client, CancellationToken.None);
                Assert.True(cache.TryGet(uri.AbsoluteUri, out var fromMemory));
                Assert.Same(bytes, fromMemory);
                Assert.Same(bytes, await cache.LoadAsync(uri, client, CancellationToken.None));
                var restarted = new ArtworkCacheStore(directory);
                Assert.Equal(bytes, await restarted.LoadAsync(uri, client, CancellationToken.None));
                Assert.Equal(1, handler.Requests);
                restarted.Invalidate(uri.AbsoluteUri);
                Assert.False(restarted.TryGet(uri.AbsoluteUri, out _));
                await restarted.LoadAsync(uri, client, CancellationToken.None);
                Assert.Equal(2, handler.Requests);
                await restarted.LoadAsync(new Uri("https://example.com/new-artwork"), client, CancellationToken.None);
                Assert.Equal(3, handler.Requests);
            }
            finally
            {
                if (Directory.Exists(directory) &&
                    Path.GetFullPath(directory).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    Directory.Delete(directory, true);
                }
            }
        }

        [Fact]
        public async Task CurrentTrackKeepsOnlyOneArtworkAndNeverCreatesDiskCache()
        {
            var directory = Path.Combine(Path.GetTempPath(), "KK.SFPlayer.Tests", Guid.NewGuid().ToString("N"));
            using var handler = new ImageHandler();
            using var client = new HttpClient(handler);
            var cache = new ArtworkCacheStore(directory, persist: false);
            var first = new Uri("https://example.com/first");
            var second = new Uri("https://example.com/second");
            await cache.LoadAsync(first, client, CancellationToken.None);
            Assert.True(cache.TryGet(first.AbsoluteUri, out _));
            await cache.LoadAsync(second, client, CancellationToken.None);
            Assert.False(cache.TryGet(first.AbsoluteUri, out _));
            Assert.True(cache.TryGet(second.AbsoluteUri, out _));
            cache.ClearMemory();
            Assert.False(cache.TryGet(second.AbsoluteUri, out _));
            Assert.False(Directory.Exists(directory));
        }

        [Fact]
        public async Task NewPlaylistSnapshotDownloadsArtworkAgainEvenWhenUrlIsUnchanged()
        {
            var directory = Path.Combine(Path.GetTempPath(), "KK.SFPlayer.Tests", Guid.NewGuid().ToString("N"));
            try
            {
                using var handler = new ImageHandler();
                using var client = new HttpClient(handler);
                var cache = new ArtworkCacheStore(directory);
                var uri = new Uri("https://example.com/cover");
                await cache.LoadAsync(uri, client, CancellationToken.None, "v1");
                await cache.LoadAsync(uri, client, CancellationToken.None, "v1");
                Assert.Equal(1, handler.Requests);
                await cache.LoadAsync(uri, client, CancellationToken.None, "v2");
                Assert.Equal(2, handler.Requests);
                await new ArtworkCacheStore(directory).LoadAsync(uri, client, CancellationToken.None, "v2");
                Assert.Equal(2, handler.Requests);
            }
            finally
            {
                var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "KK.SFPlayer.Tests")) + Path.DirectorySeparatorChar;
                if (Directory.Exists(directory) && Path.GetFullPath(directory).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    Directory.Delete(directory, true);
                }
            }
        }

        [Fact]
        public void LegacyMixedArtworkFilesAreRemovedWithoutDeletingOtherFiles()
        {
            var directory = Path.Combine(Path.GetTempPath(), "KK.SFPlayer.Tests", Guid.NewGuid().ToString("N"));
            var cacheDirectory = Path.Combine(directory, "ArtworkCache");
            try
            {
                Directory.CreateDirectory(cacheDirectory);
                var legacy = Path.Combine(cacheDirectory, new string('A', 64));
                var other = Path.Combine(cacheDirectory, "keep.txt");
                File.WriteAllText(legacy, "old artwork");
                File.WriteAllText(other, "keep");
                _ = new ArtworkCacheStore(directory);
                Assert.False(File.Exists(legacy));
                Assert.True(File.Exists(other));
            }
            finally
            {
                var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "KK.SFPlayer.Tests")) + Path.DirectorySeparatorChar;
                if (Directory.Exists(directory) && Path.GetFullPath(directory).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    Directory.Delete(directory, true);
                }
            }
        }

        private sealed class ImageHandler : HttpMessageHandler
        {
            public int Requests { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
            }
        }
    }
}
