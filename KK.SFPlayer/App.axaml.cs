using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using KK.SFPlayer.Views;
using KK.SFPlayer.Services;
using KK.SFPlayer.ViewModels;
using System;

namespace KK.SFPlayer
{
    public partial class App : Application
    {
        public override void Initialize() => AvaloniaXamlLoader.Load(this);

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var settings = new SettingsStore();
                var playback = new WebPlaybackEngine(() => desktop.MainWindow?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);
                var player = new MPlayerService(settings, playback: playback);
                var viewModel = new MainViewModel(player, settings, new StartupService());
                playback.PlaybackError += (_, message) => viewModel.Status = message;
                desktop.MainWindow = new MainWindow { DataContext = viewModel };
                var audioSession = new AudioSessionPresence();
                desktop.MainWindow.Opened += async (_, _) => await audioSession.StartAsync();
                desktop.MainWindow.Closed += (_, _) => audioSession.Dispose();
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
