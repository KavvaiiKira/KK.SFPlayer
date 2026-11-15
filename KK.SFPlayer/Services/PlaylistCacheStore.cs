using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KK.SFPlayer.Models.Records;

namespace KK.SFPlayer.Services
{
    internal sealed class PlaylistCacheStore(string dataDirectory)
    {
        private const string CacheDirectory = "PlaylistCache";
        private const string LibraryFile = "library.json";
        private const string TrackPrefix = "tracks-";
        private const string JsonExtension = ".json";
        private const string TemporarySuffix = ".tmp";
        private readonly string _root = Path.Combine(dataDirectory, CacheDirectory);
        private readonly object _sync = new();

        public PlaylistPage? LoadLibrary(string clientId)
        {
            var page = Read<PlaylistPage>(Path.Combine(ScopeDirectory(clientId), LibraryFile));
            return page is { Playlists: not null, NextOffset: >= 0 } && page.Playlists.All(playlist => playlist is not null && !string.IsNullOrWhiteSpace(playlist.Id)) ? page : null;
        }

        public void SaveLibrary(string clientId, PlaylistPage page) => Write(Path.Combine(ScopeDirectory(clientId), LibraryFile), page);

        public IReadOnlyList<CachedPlaylistData> LoadTracks(string clientId)
        {
            var result = new List<CachedPlaylistData>();
            lock (_sync)
            {
                var directory = ScopeDirectory(clientId);
                if (!Directory.Exists(directory))
                {
                    return result;
                }

                try
                {
                    foreach (var file in Directory.EnumerateFiles(directory, TrackPrefix + "*" + JsonExtension))
                    {
                        if (Read<CachedPlaylistData>(file) is { FormatVersion: 1, Tracks: not null } cached &&
                            !string.IsNullOrWhiteSpace(cached.PlaylistId) && !string.IsNullOrWhiteSpace(cached.SnapshotId) &&
                            cached.Tracks.All(track => track is not null && !string.IsNullOrWhiteSpace(track.Uri) && track.Position >= 0 && track.DurationMs >= 0))
                        {
                            result.Add(cached);
                        }
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    return result;
                }
            }

            return result;
        }

        public void SaveTracks(string clientId, CachedPlaylistData playlist)
        {
            var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(playlist.PlaylistId)));
            Write(Path.Combine(ScopeDirectory(clientId), TrackPrefix + name + JsonExtension), playlist);
        }

        public void Clear(string clientId)
        {
            lock (_sync)
            {
                var directory = ScopeDirectory(clientId);
                if (!Directory.Exists(directory))
                {
                    return;
                }

                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    File.Delete(file);
                }

                Directory.Delete(directory);
            }
        }

        public void RemoveTracks(string clientId, string playlistId)
        {
            var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(playlistId)));
            lock (_sync)
            {
                File.Delete(Path.Combine(ScopeDirectory(clientId), TrackPrefix + name + JsonExtension));
            }
        }

        private string ScopeDirectory(string clientId) => Path.Combine(_root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(clientId))));

        private T? Read<T>(string path) where T : class
        {
            lock (_sync)
            {
                try
                {
                    return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllBytes(path)) : null;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
                {
                    return null;
                }
            }
        }

        private void Write<T>(string path, T value)
        {
            lock (_sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path + TemporarySuffix, JsonSerializer.SerializeToUtf8Bytes(value));
                File.Move(path + TemporarySuffix, path, true);
            }
        }
    }
}
