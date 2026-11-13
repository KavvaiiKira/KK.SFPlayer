using KK.SFPlayer.Constants;
using System;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KK.SFPlayer.Services;

namespace KK.SFPlayer.ViewModels
{
    public partial class SettingsViewModel : ObservableObject
    {
        private const string ClientIdPattern = "\\A[0-9a-fA-F]{32}\\z";

        private readonly Action<string, bool> _save;
        private readonly Action _close;

        [ObservableProperty]
        private string _clientId = string.Empty;

        [ObservableProperty]
        private bool _startWithWindows;

        [ObservableProperty]
        private bool _startupAvailable;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasError))]
        private string _error = string.Empty;

        internal SettingsViewModel(Action<string, bool> save, Action close)
        {
            _save = save;
            _close = close;
        }

        public bool HasError => !string.IsNullOrEmpty(Error);

        internal static bool IsValidClientId(string? clientId) =>
            clientId is not null && Regex.IsMatch(clientId, ClientIdPattern);

        internal void Open(string clientId, bool startWithWindows, bool startupAvailable)
        {
            ClientId = clientId;
            StartWithWindows = startWithWindows;
            StartupAvailable = startupAvailable;
            Error = string.Empty;
        }

        [RelayCommand]
        private void Save()
        {
            var clientId = ClientId.Trim();
            if (clientId.Length > 0 && !IsValidClientId(clientId))
            {
                Error = SettingsConstants.InvalidClientId;
                return;
            }

            try
            {
                _save(clientId, StartWithWindows);
                _close();
            }
            catch (Exception exception)
            {
                Error = ErrorMessages.From(exception);
            }
        }

        [RelayCommand]
        private void Cancel() => _close();
    }
}
