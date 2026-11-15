using KK.SFPlayer.Constants;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using KK.SFPlayer.Models.Records;
using SpotifyAPI.Web;

namespace KK.SFPlayer.Services
{
    internal sealed class SettingsStore
    {
        private const string SettingsFile = "settings.json";
        private const string TokenFile = "spotify-token.dat";
        private const string TemporarySuffix = ".tmp";

        private readonly string _directory;
        private readonly object _sync = new();

        internal string DataDirectory => _directory;

        public SettingsStore(string? directory = null) => _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ApplicationConstants.AppName);

        public AppSettings Load()
        {
            lock (_sync)
            {
                var path = Path.Combine(_directory, SettingsFile);
                if (!File.Exists(path))
                {
                    return new AppSettings();
                }

                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings();
            }
        }

        public void Save(AppSettings settings)
        {
            lock (_sync)
            {
                WriteAtomic(SettingsFile, JsonSerializer.SerializeToUtf8Bytes(settings));
            }
        }

        public PKCETokenResponse? LoadToken(string clientId)
        {
            lock (_sync)
            {
                var path = Path.Combine(_directory, TokenFile);
                if (!File.Exists(path) || !OperatingSystem.IsWindows())
                {
                    return null;
                }

                var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
                var saved = JsonSerializer.Deserialize<SavedToken>(bytes);
                return saved?.ClientId == clientId ? saved.Token : null;
            }
        }

        public void SaveToken(string clientId, PKCETokenResponse token)
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            lock (_sync)
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new SavedToken(clientId, token));
                WriteAtomic(TokenFile, ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
            }
        }

        public void ClearToken()
        {
            lock (_sync)
            {
                var path = Path.Combine(_directory, TokenFile);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        private void WriteAtomic(string name, byte[] content)
        {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, name);
            File.WriteAllBytes(path + TemporarySuffix, content);
            File.Move(path + TemporarySuffix, path, true);
        }

    }
}
