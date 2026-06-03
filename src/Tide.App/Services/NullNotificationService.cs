using Tide.Core;

namespace Tide.App.Services;

public sealed class NullNotificationService : INotificationService
{
    public event EventHandler<string>? Activated
    {
        add { }
        remove { }
    }
    public bool IsSupported => false;
    public string StatusMessage => "通知はこの環境では利用できません。";
    public void Initialize() { }
    public Task ShowNewStoriesAsync(NotificationPayload payload, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public void Dispose() { }
}
