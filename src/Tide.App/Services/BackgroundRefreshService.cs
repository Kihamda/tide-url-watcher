using Tide.Core;

namespace Tide.App.Services;

public sealed class BackgroundRefreshService : IAsyncDisposable
{
    private readonly TideRepository _repository;
    private readonly INotificationService _notifications;
    private readonly TideLogger _logger;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;

    public BackgroundRefreshService(TideRepository repository, INotificationService notifications, TideLogger logger)
    {
        _repository = repository;
        _notifications = notifications;
        _logger = logger;
    }

    public event EventHandler<RefreshServiceState>? StateChanged;
    public event EventHandler<RefreshResult>? RefreshCompleted;

    public Snapshot Snapshot { get; private set; } = new();
    public RefreshActivityState State { get; private set; } = RefreshActivityState.Idle;
    public DateTimeOffset? NextRefreshAt { get; private set; }
    public bool NotificationsPaused => Snapshot.Settings.PauseNotificationsUntil is not null &&
                                       Snapshot.Settings.PauseNotificationsUntil > DateTimeOffset.Now;

    public void Start(Snapshot snapshot)
    {
        Snapshot = snapshot.Normalize();
        StopLoop();
        _loopCts = new CancellationTokenSource();
        _loopTask = RunLoopAsync(_loopCts.Token);
        Report(RefreshActivityState.Idle, "自動取得を待機しています。");
    }

    public void UpdateSnapshot(Snapshot snapshot)
    {
        Snapshot = snapshot.Normalize();
        ScheduleNext(DateTimeOffset.Now);
        Report(State, null);
    }

    public async Task<RefreshResult?> RefreshNowAsync(bool manual, CancellationToken cancellationToken = default)
    {
        if (!await _refreshGate.WaitAsync(0, cancellationToken))
        {
            return null;
        }

        try
        {
            Report(RefreshActivityState.Refreshing, "更新中...");
            var result = await _repository.RefreshAsync(Snapshot, force: manual, cancellationToken);
            Snapshot = result.Snapshot.Normalize();
            ScheduleNext(DateTimeOffset.Now);
            RefreshCompleted?.Invoke(this, result);

            var payload = NotificationPlanner.BuildNewStoriesPayload(Snapshot, result.NewStories, DateTimeOffset.Now, manual);
            if (payload is not null)
            {
                await _notifications.ShowNewStoriesAsync(payload, cancellationToken);
                Snapshot = await _repository.MarkStoriesNotifiedAsync(
                    Snapshot,
                    payload.Stories.Select(story => story.Id),
                    cancellationToken);
            }

            Report(result.FailedSources.Count == 0 ? RefreshActivityState.Idle : RefreshActivityState.Error,
                result.FailedSources.Count == 0 ? "更新しました。" : $"{result.FailedSources.Count}件のサイトで取得に失敗しました。");
            return result with { Snapshot = Snapshot };
        }
        catch (OperationCanceledException)
        {
            Report(RefreshActivityState.Paused, "更新を停止しました。");
            return null;
        }
        catch (Exception exception)
        {
            _logger.Error("Background refresh failed.", exception);
            Report(RefreshActivityState.Error, "バックグラウンド更新に失敗しました。");
            return null;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public async Task PauseNotificationsForAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        Snapshot = await _repository.SaveSettingsAsync(Snapshot, Snapshot.Settings with
        {
            PauseNotificationsUntil = DateTimeOffset.Now.Add(duration)
        }, cancellationToken);
        Report(RefreshActivityState.Paused, "通知を一時停止しました。");
    }

    public async Task ResumeNotificationsAsync(CancellationToken cancellationToken = default)
    {
        Snapshot = await _repository.SaveSettingsAsync(Snapshot, Snapshot.Settings with
        {
            PauseNotificationsUntil = null
        }, cancellationToken);
        Report(RefreshActivityState.Idle, "通知を再開しました。");
    }

    public async ValueTask DisposeAsync()
    {
        StopLoop();
        if (_loopTask is not null)
        {
            try
            {
                await _loopTask;
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
        }

        _refreshGate.Dispose();
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        if (Snapshot.Settings.RefreshOnStartup && Snapshot.Settings.AutoRefreshEnabled)
        {
            _ = Task.Run(() => RefreshNowAsync(manual: false, cancellationToken), cancellationToken);
        }

        ScheduleNext(DateTimeOffset.Now);
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!Snapshot.Settings.AutoRefreshEnabled)
            {
                NextRefreshAt = null;
                Report(RefreshActivityState.Paused, "自動取得はオフです。");
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                continue;
            }

            var delay = NextRefreshAt is null
                ? TimeSpan.FromMinutes(Snapshot.Settings.RefreshIntervalMinutes)
                : NextRefreshAt.Value - DateTimeOffset.Now;
            if (delay < TimeSpan.FromSeconds(1))
            {
                delay = TimeSpan.FromSeconds(1);
            }

            await Task.Delay(delay, cancellationToken);
            await RefreshNowAsync(manual: false, cancellationToken);
        }
    }

    private void ScheduleNext(DateTimeOffset now)
    {
        var interval = Math.Clamp(Snapshot.Settings.RefreshIntervalMinutes, 5, 1440);
        NextRefreshAt = Snapshot.Settings.AutoRefreshEnabled ? now.AddMinutes(interval) : null;
    }

    private void StopLoop()
    {
        try
        {
            _loopCts?.Cancel();
            _loopCts?.Dispose();
        }
        catch
        {
            // Best-effort shutdown.
        }
    }

    private void Report(RefreshActivityState state, string? message)
    {
        State = state;
        StateChanged?.Invoke(this, new RefreshServiceState(state, message, NextRefreshAt));
    }
}

public sealed record RefreshServiceState(RefreshActivityState State, string? Message, DateTimeOffset? NextRefreshAt);
