namespace KK.SFPlayer.Constants
{
    public static class AuthenticationConstants
    {
        public const string SignIn = "Log in";
        public const string SignOut = "Log out";
        public const string ConfirmLogout = "Are you sure you want to log out?";
        public const string RedirectUri = "http://127.0.0.1:5543/callback/";
        public const string Connecting = "Sign in to Spotify in your browser…";
        public const string NotConnected = "Sign in to Spotify.";
        public const string AuthDenied = "Spotify sign-in was cancelled or rejected. Try signing in again.";
        public const string AuthTimeout = "Spotify sign-in timed out. Try signing in again.";
        public const string AuthListenerFailed = "Cannot start Spotify sign-in. Check that port 5543 is free.";
        public const string AuthCallbackSuccess = "Signed in. You can close this tab and return to KK.SFPlayer.";
        public const string AuthCallbackFailed = "Sign-in failed. Return to KK.SFPlayer and try again.";
        public const string Reconnect = "Spotify authorization expired or was revoked. Sign in again.";
    }
}
