using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Tide.Core;

namespace Tide.App.Services;

public sealed class WindowsNotificationService : INotificationService
{
    private readonly TideLogger _logger;
    private bool _registered;

    public WindowsNotificationService(TideLogger logger)
    {
        _logger = logger;
    }

    public event EventHandler<string>? Activated;
    public bool IsSupported { get; private set; }
    public string StatusMessage { get; private set; } = "通知は未初期化です。";

    public void Initialize()
    {
        try
        {
            var manager = AppNotificationManager.Default;
            manager.NotificationInvoked += OnNotificationInvoked;
            manager.Register();
            _registered = true;
            IsSupported = true;
            StatusMessage = "Windowsローカル通知が有効です。";
            _logger.Info("Notification service registered.");
        }
        catch (Exception exception)
        {
            IsSupported = false;
            StatusMessage = "Windows通知を初期化できませんでした。管理者権限や通知設定を確認してください。";
            _logger.Warn("Notification registration failed.", exception);
        }
    }

    public Task ShowNewStoriesAsync(NotificationPayload payload, CancellationToken cancellationToken = default)
    {
        if (!IsSupported || cancellationToken.IsCancellationRequested)
        {
            return Task.CompletedTask;
        }

        try
        {
            var builder = new AppNotificationBuilder()
                .AddArgument("action", "openApp")
                .AddText(payload.Title)
                .AddText(payload.Body)
                .AddButton(new AppNotificationButton("Tideで確認").AddArgument("action", "openApp"))
                .AddButton(new AppNotificationButton("通知を1時間停止").AddArgument("action", "pause1h"));

            if (payload.Stories.Count == 1)
            {
                builder.AddArgument("storyId", payload.Stories[0].Id);
                builder.AddButton(new AppNotificationButton("記事を開く")
                    .AddArgument("action", "openStory")
                    .AddArgument("storyId", payload.Stories[0].Id));
            }

            AppNotificationManager.Default.Show(builder.BuildNotification());
            _logger.Info($"Notification shown: {payload.Title}");
        }
        catch (Exception exception)
        {
            StatusMessage = "通知の送信に失敗しました。";
            _logger.Warn("Notification show failed.", exception);
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        try
        {
            if (_registered)
            {
                AppNotificationManager.Default.NotificationInvoked -= OnNotificationInvoked;
                AppNotificationManager.Default.Unregister();
            }
        }
        catch
        {
            // Shutdown cleanup is best-effort.
        }
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args) =>
        Activated?.Invoke(this, args.Argument);
}
