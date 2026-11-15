using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KK.SFPlayer.Services;

namespace KK.SFPlayer.ViewModels
{
    public partial class PlayerUiState : ObservableObject, IDisposable
    {
        private readonly CancellationTokenSource _lifetime = new();
        private readonly HashSet<Task> _pending = [];
        private int _activeOperations;
        private Func<CancellationToken, Task>? _retryAction;
        private bool _disposed;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanEdit))]
        [NotifyPropertyChangedFor(nameof(CanUsePlayer))]
        [NotifyPropertyChangedFor(nameof(CanRetry))]
        [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
        private bool _isBusy;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanUsePlayer))]
        private bool _isConnected;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanEdit))]
        [NotifyPropertyChangedFor(nameof(CanUsePlayer))]
        [NotifyPropertyChangedFor(nameof(CanRetry))]
        [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
        private bool _isBlocked;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasStatus))]
        private string _status = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanRetry))]
        [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
        private bool _hasRetry;

        public PlayerUiState() => CancellationToken = _lifetime.Token;

        public event Action? AuthorizationLost;

        public CancellationToken CancellationToken { get; }

        public bool HasStatus => !string.IsNullOrEmpty(Status);

        public bool CanEdit => !IsBusy && !IsBlocked;

        public bool CanUsePlayer => IsConnected && CanEdit;

        public bool CanRetry => HasRetry && CanEdit;

        internal Task Completion => Task.WhenAll(_pending.ToArray());

        public Task RunAsync(Func<CancellationToken, Task> action, bool retryable = false)
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            var task = RunCoreAsync(action, retryable);
            _pending.Add(task);
            _ = ObserveCompletionAsync(task);
            return task;
        }

        public void ClearRetry()
        {
            _retryAction = null;
            HasRetry = false;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            IsBlocked = true;
            ClearRetry();
            _lifetime.Cancel();
            _lifetime.Dispose();
        }

        [RelayCommand(CanExecute = nameof(CanRetry))]
        private Task RetryAsync() => _retryAction is { } action ? RunAsync(action, true) : Task.CompletedTask;

        private async Task ObserveCompletionAsync(Task task)
        {
            try
            {
                await task;
            }
            finally
            {
                _pending.Remove(task);
            }
        }

        private async Task RunCoreAsync(Func<CancellationToken, Task> action, bool retryable)
        {
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
            _activeOperations++;
            IsBusy = true;
            ClearRetry();
            Status = string.Empty;
            try
            {
                await action(operation.Token);
            }
            catch (OperationCanceledException) when (operation.IsCancellationRequested)
            {
                Status = string.Empty;
            }
            catch (Exception exception)
            {
                if (ErrorMessages.IsAuthenticationFailure(exception))
                {
                    AuthorizationLost?.Invoke();
                }

                Status = ErrorMessages.From(exception);
                if (retryable && ErrorMessages.CanRetry(exception))
                {
                    _retryAction = action;
                    HasRetry = true;
                }
            }
            finally
            {
                _activeOperations--;
                if (_activeOperations == 0)
                {
                    IsBusy = false;
                }
            }
        }
    }
}
