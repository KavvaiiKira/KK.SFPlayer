using KK.SFPlayer.Constants;
using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using SpotifyAPI.Web;

namespace KK.SFPlayer.Services
{
    internal static class ErrorMessages
    {
        public static bool CanRetry(Exception exception) => exception switch
        {
            APIException api when api.Response is not null => TransientRetryHandler.IsTransient(api.Response.StatusCode),
            HttpRequestException request => request.StatusCode is null || TransientRetryHandler.IsTransient(request.StatusCode.Value),
            OperationCanceledException => true,
            _ => false,
        };

        public static string From(Exception exception) => exception switch
        {
            APIException api => (int?)api.Response?.StatusCode switch
            {
                400 or 401 => AuthenticationConstants.Reconnect,
                403 => NetworkConstants.Forbidden,
                404 => PlaybackConstants.NoDevice,
                429 => NetworkConstants.RateLimited,
                _ => NetworkConstants.SpotifyError,
            },
            HttpRequestException => NetworkConstants.NetworkError,
            OperationCanceledException => NetworkConstants.RequestTimeout,
            IOException or UnauthorizedAccessException or JsonException or CryptographicException => SettingsConstants.SettingsError,
            InvalidOperationException => exception.Message,
            _ => NetworkConstants.SpotifyError,
        };
    }
}
