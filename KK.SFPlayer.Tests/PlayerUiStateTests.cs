using System;
using System.Threading;
using System.Threading.Tasks;
using KK.SFPlayer.ViewModels;
using Xunit;

namespace KK.SFPlayer.Tests
{
    public sealed class PlayerUiStateTests
    {
        [Fact]
        public async Task BusyStateRemainsUntilEveryPendingOperationCompletes()
        {
            using var state = new PlayerUiState();
            var firstResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = state.RunAsync(_ => firstResponse.Task);
            var second = state.RunAsync(_ => secondResponse.Task);
            secondResponse.SetResult();
            await second;
            Assert.True(state.IsBusy);
            Assert.False(state.CanEdit);
            firstResponse.SetResult();
            await first;
            Assert.False(state.IsBusy);
            Assert.True(state.CanEdit);
        }

        [Fact]
        public async Task DisposingSharedStateCancelsRequestsWithoutOfferingRetry()
        {
            using var state = new PlayerUiState();
            var request = state.RunAsync(token => Task.Delay(Timeout.Infinite, token), true);
            Assert.True(state.IsBusy);
            state.Dispose();
            await request.WaitAsync(TimeSpan.FromSeconds(2));
            await state.Completion;
            Assert.True(state.CancellationToken.IsCancellationRequested);
            Assert.False(state.HasRetry);
            Assert.False(state.IsBusy);
            Assert.Empty(state.Status);
            Assert.False(state.CanEdit);
        }
    }
}
