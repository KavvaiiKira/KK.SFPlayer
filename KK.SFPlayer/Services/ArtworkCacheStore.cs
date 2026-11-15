using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KK.SFPlayer.Constants;

namespace KK.SFPlayer.Services
{
    internal sealed class ArtworkCacheStore
    {
        private const int MemoryLimit = 128;
        private const int DiskLimit = 256;
        private const string CacheDirectory = "ArtworkCache";
        private readonly string _directory;
        private readonly bool _persist;
        private readonly int _memoryLimit;
        private readonly Dictionary<string, byte[]> _memory = [];
        private readonly object _sync = new();

        public ArtworkCacheStore(string? dataDirectory = null, bool persist = true)
        {
            _persist = persist;
            _memoryLimit = persist ? MemoryLimit : 1;
            var root = Path.Combine(
                dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ApplicationConstants.AppName),
                CacheDirectory);
            _directory = Path.Combine(root, "Playlists");
            if (!persist)
            {
                return;
            }
            try
            {
                if (Directory.Exists(root))
                {
                    foreach (var file in Directory.EnumerateFiles(root))
                    {
                        var name = Path.GetFileName(file);
                        if (name.Length == 64 && name.All(Uri.IsHexDigit))
                        {
                            TryDelete(file);
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        public bool TryGet(string url, out byte[] bytes, string? version = null)
        {
            lock (_sync)
            {
                return _memory.TryGetValue(GetKey(url, version), out bytes!);
            }
        }

        public async Task<byte[]> LoadAsync(Uri uri, HttpClient client, CancellationToken cancellationToken, string? version = null)
        {
            var url = uri.AbsoluteUri;
            var key = GetKey(url, version);
            if (TryGet(url, out var cached, version))
            {
                return cached;
            }

            var path = GetPath(key);
            try
            {
                if (_persist && File.Exists(path))
                {
                    var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
                    Remember(key, bytes);
                    return bytes;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }

            var downloaded = await client.GetByteArrayAsync(uri, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Remember(key, downloaded);
            if (!_persist)
            {
                return downloaded;
            }
            var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(_directory);
                await File.WriteAllBytesAsync(temporaryPath, downloaded, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryPath, path, true);
                foreach (var file in new DirectoryInfo(_directory).EnumerateFiles()
                    .Where(file => file.Name.Length == 64 && file.Name.All(Uri.IsHexDigit))
                    .OrderByDescending(file => file.LastWriteTimeUtc).Skip(DiskLimit))
                {
                    TryDelete(file.FullName);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
            finally
            {
                TryDelete(temporaryPath);
            }

            return downloaded;
        }

        public void ClearMemory()
        {
            lock (_sync)
            {
                _memory.Clear();
            }
        }

        public void Invalidate(string url, string? version = null)
        {
            lock (_sync)
            {
                _memory.Remove(GetKey(url, version));
            }
            if (!_persist)
            {
                return;
            }
            TryDelete(GetPath(GetKey(url, version)));
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        private static string GetKey(string url, string? version) => string.IsNullOrEmpty(version) ? url : url + "\n" + version;

        private string GetPath(string url) => Path.Combine(_directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))));

        private void Remember(string url, byte[] bytes)
        {
            lock (_sync)
            {
                if (_memory.Count >= _memoryLimit && !_memory.ContainsKey(url))
                {
                    _memory.Remove(_memory.Keys.First());
                }
                _memory[url] = bytes;
            }
        }
    }
}
