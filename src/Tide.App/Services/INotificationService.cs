using Tide.Core;

namespace Tide.App.Services;

public interface INotificationService : IDisposable
{
    event EventHandler<string>? Activated;
    bool IsSupported { get; }
    string StatusMessage { get; }
    void Initialize();
    Task ShowNewStoriesAsync(NotificationPayload payload, CancellationToken cancellationToken = default);
}
