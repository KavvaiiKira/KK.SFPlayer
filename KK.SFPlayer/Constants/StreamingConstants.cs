namespace KK.SFPlayer.Constants
{
    public static class StreamingConstants
    {
        public const string Host = "kk-sfplayer";
        public const string Page = "https://" + Host + "/index.html";
        public const string ProfileDirectory = "PlaybackProfile";
        public const string Unsupported = "In-app playback requires Windows and Microsoft Edge WebView2 Runtime.";
        public const string RuntimeMissing = "Install Microsoft Edge WebView2 Runtime to play music in this app.";
        public const string Reauthorize = "Log out and log in again to allow in-app playback.";
        public const string PremiumRequired = "Spotify Premium is required for in-app playback.";
        public const string InitializationFailed = "Cannot start the audio player. Check your connection and try again.";
        public const string PlaybackFailed = "Spotify could not play this track. Try again.";
        public const string DeviceUnavailable = "The in-app player disconnected. Press Play to reconnect.";
        public const string Pause = "Pause";
        public const string Play = "Play";
        public const string Resume = "Resume";
        public const string Volume = "Volume";
        public const string Previous = "Previous track";
        public const string Next = "Next track";
        public const string ShuffleOn = "Shuffle: on";
        public const string ShuffleOff = "Shuffle: off";
        public const string RepeatOff = "Repeat: off";
        public const string RepeatAll = "Repeat: playlist";
        public const string RepeatOne = "Repeat: one track";
        public const string RepeatOneBadge = "1";
        public const string Seek = "Seek";
        public const string RemainingTimeFormat = "-{0}";
        public const double DefaultVolume = 0.1;
        public const int VolumePopupCloseDelayMilliseconds = 200;
        public const int ConnectionTimeoutSeconds = 40;
        public const int ModeConfirmationTimeoutSeconds = 5;
    }
}
