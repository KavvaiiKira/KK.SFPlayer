using KK.SFPlayer.Constants;
using System;
using System.IO;
using Microsoft.Win32;

namespace KK.SFPlayer.Services
{
    internal sealed class StartupService : IStartupService
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public bool IsSupported => OperatingSystem.IsWindows();

        public bool IsEnabled
        {
            get
            {
                if (!OperatingSystem.IsWindows())
                {
                    return false;
                }

                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ApplicationConstants.AppName) is string;
            }
        }

        public void SetEnabled(bool enabled)
        {
            if (!OperatingSystem.IsWindows())
            {
                if (enabled)
                {
                    throw new InvalidOperationException(SettingsConstants.StartupUnsupported);
                }

                return;
            }

            using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
            if (enabled)
            {
                var executable = Path.Combine(AppContext.BaseDirectory, ApplicationConstants.ExecutableFileName);
                if (!File.Exists(executable))
                {
                    throw new InvalidOperationException(SettingsConstants.StartupError);
                }

                key.SetValue(ApplicationConstants.AppName, $"\"{executable}\"");
            }
            else
            {
                key.DeleteValue(ApplicationConstants.AppName, false);
            }
        }
    }
}
