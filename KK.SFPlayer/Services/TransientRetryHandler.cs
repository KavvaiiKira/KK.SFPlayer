using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace KK.SFPlayer.Services
{
    internal sealed class TransientRetryHandler : DelegatingHandler
    {
        private const int MaximumRetries = 2;
        private static readonly TimeSpan MaximumServerDelay = TimeSpan.FromSeconds(5);
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;
        private readonly TimeSpan _attemptTimeout;

        public TransientRetryHandler(
            HttpMessageHandler? innerHandler = null,
            Func<TimeSpan, CancellationToken, Task>? delay = null,
            TimeSpan? attemptTimeout = null)
            : base(innerHandler ?? new HttpClientHandler())
        {
            _delay = delay ?? Task.Delay;
            _attemptTimeout = attemptTimeout ?? TimeSpan.FromSeconds(5);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Get)
            {
                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }

            for (var attempt = 0; ; attempt++)
            {
                var delay = TimeSpan.FromMilliseconds(300 * (attempt + 1));
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_attemptTimeout);
                HttpResponseMessage? response = null;
                try
                {
                    response = await base.SendAsync(request, timeout.Token).ConfigureAwait(false);
                    var serverDelay = response.Headers.RetryAfter?.Delta ??
                        response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow;
                    if (!IsTransient(response.StatusCode) || attempt >= MaximumRetries || serverDelay > MaximumServerDelay)
                    {
                        await response.Content.LoadIntoBufferAsync(timeout.Token).ConfigureAwait(false);
                        return response;
                    }

                    if (serverDelay.HasValue)
                    {
                        delay = serverDelay.Value > TimeSpan.Zero ? serverDelay.Value : TimeSpan.Zero;
                    }
                }
                catch (Exception exception) when (
                    attempt < MaximumRetries &&
                    !cancellationToken.IsCancellationRequested &&
                    IsTransient(exception))
                {
                    response?.Dispose();
                    response = null;
                }
                catch
                {
                    response?.Dispose();
                    throw;
                }

                response?.Dispose();
                await _delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        internal static bool IsTransient(HttpStatusCode status) => status is
            HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or
            HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

        internal static bool IsTransient(Exception exception) => exception switch
        {
            HttpRequestException request => request.StatusCode is null || IsTransient(request.StatusCode.Value),
            OperationCanceledException or IOException => true,
            _ => false,
        };
    }
}
