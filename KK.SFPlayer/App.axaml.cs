using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using KK.SFPlayer.Views;
using KK.SFPlayer.Services;
using KK.SFPlayer.ViewModels;

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
                var player = new MPlayerService(settings);
                var viewModel = new MainViewModel(player, settings, new StartupService());
                desktop.MainWindow = new MainWindow { DataContext = viewModel };
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
