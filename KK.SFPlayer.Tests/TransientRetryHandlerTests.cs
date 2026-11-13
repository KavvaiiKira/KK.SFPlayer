using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using KK.SFPlayer.Services;
using Xunit;

namespace KK.SFPlayer.Tests
{
    public sealed class TransientRetryHandlerTests
    {
        [Theory]
        [InlineData(HttpStatusCode.RequestTimeout)]
        [InlineData(HttpStatusCode.InternalServerError)]
        [InlineData(HttpStatusCode.BadGateway)]
        [InlineData(HttpStatusCode.ServiceUnavailable)]
        [InlineData(HttpStatusCode.GatewayTimeout)]
        public async Task TemporaryReadFailureCanRecover(HttpStatusCode status)
        {
            var transport = new FakeTransport((attempt, _) => Task.FromResult(Response(attempt == 1 ? status : HttpStatusCode.OK)));
            using var client = CreateClient(transport);

            Assert.Equal("response", await client.GetStringAsync("https://example.com/artwork"));

            Assert.Equal(2, transport.Attempts);
        }

        [Fact]
        public async Task ConnectionFailureCanRecover()
        {
            var transport = new FakeTransport((attempt, _) => attempt == 1 ?
                Task.FromException<HttpResponseMessage>(new HttpRequestException()) :
                Task.FromResult(Response(HttpStatusCode.OK)));
            using var client = CreateClient(transport);

            Assert.Equal("response", await client.GetStringAsync("https://example.com/artwork"));
            Assert.Equal(2, transport.Attempts);
        }

        [Theory]
        [InlineData(HttpStatusCode.NotFound)]
        [InlineData(HttpStatusCode.Unauthorized)]
        [InlineData(HttpStatusCode.Forbidden)]
        public async Task PermanentFailureIsNotRetried(HttpStatusCode status)
        {
            var transport = new FakeTransport((_, _) => Task.FromResult(Response(status)));
            using var client = CreateClient(transport);

            using var response = await client.GetAsync("https://example.com/artwork");

            Assert.Equal(status, response.StatusCode);
            Assert.Equal(1, transport.Attempts);
        }

        [Fact]
        public async Task RetriesAreLimitedToTwo()
        {
            var transport = new FakeTransport((_, _) => Task.FromResult(Response(HttpStatusCode.ServiceUnavailable)));
            using var client = CreateClient(transport);

            using var response = await client.GetAsync("https://example.com/artwork");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal(3, transport.Attempts);
        }

        [Fact]
        public async Task PlaybackCommandsAreNotAutomaticallyRepeated()
        {
            var transport = new FakeTransport((_, _) => Task.FromResult(Response(HttpStatusCode.ServiceUnavailable)));
            using var client = CreateClient(transport);

            using var response = await client.PutAsync("https://example.com/player/play", new StringContent("{}"));

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal(1, transport.Attempts);
        }

        [Fact]
        public async Task ServerRetryAfterIsRespected()
        {
            var transport = new FakeTransport((attempt, _) =>
            {
                var response = Response(attempt == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
                return Task.FromResult(response);
            });
            var delays = new List<TimeSpan>();
            using var client = new HttpClient(new TransientRetryHandler(transport, (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            }));

            await client.GetStringAsync("https://example.com/artwork");

            Assert.Equal(TimeSpan.FromSeconds(2), Assert.Single(delays));
        }

        [Fact]
        public async Task LongServerCooldownIsReturnedToCallerWithoutRetryingEarly()
        {
            var transport = new FakeTransport((_, _) =>
            {
                var response = Response(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(1));
                return Task.FromResult(response);
            });
            using var client = CreateClient(transport);

            using var response = await client.GetAsync("https://example.com/artwork");

            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.Equal(1, transport.Attempts);
        }

        [Fact]
        public async Task CancellationDoesNotStartAnotherAttempt()
        {
            using var cancellation = new CancellationTokenSource();
            var transport = new FakeTransport((_, token) =>
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.FromResult(Response(HttpStatusCode.OK));
            });
            using var client = CreateClient(transport);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync("https://example.com/artwork", cancellation.Token));

            Assert.Equal(1, transport.Attempts);
        }

        [Fact]
        public async Task TimedOutAttemptsAreBounded()
        {
            var transport = new FakeTransport(async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return Response(HttpStatusCode.OK);
            });
            using var client = new HttpClient(new TransientRetryHandler(
                transport,
                (_, _) => Task.CompletedTask,
                TimeSpan.FromMilliseconds(50)));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync("https://example.com/artwork"));

            Assert.Equal(3, transport.Attempts);
        }

        private static HttpClient CreateClient(FakeTransport transport) =>
            new(new TransientRetryHandler(transport, (_, _) => Task.CompletedTask));

        private static HttpResponseMessage Response(HttpStatusCode status) => new(status)
        {
            Content = new StringContent("response"),
        };

        private sealed class FakeTransport(Func<int, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
        {
            public int Attempts { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                send(++Attempts, cancellationToken);
        }
    }
}
