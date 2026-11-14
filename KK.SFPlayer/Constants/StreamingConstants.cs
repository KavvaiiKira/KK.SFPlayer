namespace KK.SFPlayer.Constants
{
    public static class StreamingConstants
    {
        public const string Host = "kk-sfplayer.test";
        public const string Page = "https://kk-sfplayer.test/index.html";
        public const string ProfileDirectory = "PlaybackProfile";
        public const string Unsupported = "In-app playback requires Windows and Microsoft Edge WebView2 Runtime.";
        public const string RuntimeMissing = "Install Microsoft Edge WebView2 Runtime to play music in this app.";
        public const string Reauthorize = "Log out and log in again to allow in-app playback.";
        public const string PremiumRequired = "Spotify Premium is required for in-app playback.";
        public const string InitializationFailed = "Cannot start the audio player. Check your connection and try again.";
        public const string PlaybackFailed = "Spotify could not play this track. Try again.";
        public const string DeviceUnavailable = "The in-app player disconnected. Press Play to reconnect.";
        public const string PlaybackStarted = "Playing in KK.SFPlayer.";
        public const string Pause = "Pause";
        public const string Play = "Play";
        public const string Resume = "Resume";
        public const string Volume = "Volume";
        public const double DefaultVolume = 0.1;
        public const int ConnectionTimeoutSeconds = 40;
    }
}
