using System.Text.Json.Serialization;

namespace Tide.Core;

public sealed record AppSettings
{
    public bool AutoRefreshEnabled { get; init; } = true;
    public int RefreshIntervalMinutes { get; init; } = 30;
    public bool RefreshOnStartup { get; init; } = true;
    public bool RefreshOnWake { get; init; } = true;
    public bool CloseToTray { get; init; } = true;
    public bool MinimizeToTray { get; init; }
    public bool NotificationsEnabled { get; init; } = true;
    public bool NotifyOnManualRefresh { get; init; } = true;
    public bool LaunchAtStartup { get; init; }
    public bool StartMinimized { get; init; }
    public bool StartInTray { get; init; }
    public bool QuietHoursEnabled { get; init; }
    public string QuietHoursStart { get; init; } = "22:00";
    public string QuietHoursEnd { get; init; } = "07:00";
    public DateTimeOffset? PauseNotificationsUntil { get; init; }
    public int MaxStories { get; init; } = 500;
    public string NotificationBatching { get; init; } = "summary";

    public AppSettings Normalize() => this with
    {
        RefreshIntervalMinutes = Math.Clamp(RefreshIntervalMinutes <= 0 ? 30 : RefreshIntervalMinutes, 5, 1440),
        MaxStories = Math.Clamp(MaxStories <= 0 ? 500 : MaxStories, 50, 10000),
        QuietHoursStart = NormalizeClock(QuietHoursStart, "22:00"),
        QuietHoursEnd = NormalizeClock(QuietHoursEnd, "07:00"),
        NotificationBatching = string.IsNullOrWhiteSpace(NotificationBatching) ? "summary" : NotificationBatching
    };

    private static string NormalizeClock(string value, string fallback) =>
        TimeOnly.TryParse(value, out var parsed) ? parsed.ToString("HH:mm") : fallback;
}

public sealed record Source
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Url { get; init; }
    public string? FeedUrl { get; init; }
    public required string Kind { get; init; }
    public string? IconUrl { get; init; }
    public required string Accent { get; init; }
    public required DateTimeOffset AddedAt { get; init; }
    public bool IsEnabled { get; init; } = true;
    public bool NotificationsEnabled { get; init; } = true;
    public int? CustomIntervalMinutes { get; init; }
    public DateTimeOffset? LastCheckedAt { get; init; }
    public DateTimeOffset? LastSucceededAt { get; init; }
    public string? LastError { get; init; }
    public int ConsecutiveFailureCount { get; init; }
    public string? ETag { get; init; }
    public string? LastModified { get; init; }
}

public sealed record Story
{
    public required string Id { get; init; }
    public required string SourceId { get; init; }
    public required string Title { get; init; }
    public required string Summary { get; init; }
    public required string Url { get; init; }
    public string? ImageUrl { get; init; }
    public required DateTimeOffset PublishedAt { get; init; }
    public required DateTimeOffset DiscoveredAt { get; init; }
    public bool IsRead { get; init; }
    public bool IsSaved { get; init; }
    public DateTimeOffset? ReadAt { get; init; }
    public DateTimeOffset? SavedAt { get; init; }
    public DateTimeOffset? NotifiedAt { get; init; }
    public bool WasNotified { get; init; }
    public bool IsInitial { get; init; }
    public string? ExternalId { get; init; }
    public string? CanonicalUrl { get; init; }
}

public sealed record Snapshot
{
    public string SchemaVersion { get; init; } = "2";
    public AppSettings Settings { get; init; } = new();
    public List<Source> Sources { get; init; } = [];
    public List<Story> Stories { get; init; } = [];
    public DateTimeOffset? LastRefreshedAt { get; init; }

    [JsonIgnore]
    public string? DataWarning { get; init; }

    public Snapshot Normalize()
    {
        var settings = (Settings ?? new AppSettings()).Normalize();
        return this with
        {
            SchemaVersion = string.IsNullOrWhiteSpace(SchemaVersion) ? "2" : SchemaVersion,
            Settings = settings,
            Sources = Sources
                .Select(source => source with
                {
                    IsEnabled = source.IsEnabled,
                    NotificationsEnabled = source.NotificationsEnabled,
                    CustomIntervalMinutes = source.CustomIntervalMinutes is null
                        ? null
                        : Math.Clamp(source.CustomIntervalMinutes.Value, 5, 1440)
                })
                .ToList(),
            Stories = Stories
                .Select(story => story with
                {
                    CanonicalUrl = string.IsNullOrWhiteSpace(story.CanonicalUrl) ? story.Url : story.CanonicalUrl
                })
                .ToList()
        };
    }
}

public sealed record FailedSource(string SourceId, string Title, string Message);

public sealed record RefreshResult(
    Snapshot Snapshot,
    IReadOnlyList<Story> NewStories,
    IReadOnlyList<FailedSource> FailedSources,
    int CheckedSourcesCount,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    bool WasCancelled)
{
    public int NewStoriesCount => NewStories.Count;
}

public sealed record ParsedFeed(string? Title, IReadOnlyList<Story> Stories);

public sealed record SourcePreview(Source Source, IReadOnlyList<Story> InitialStories, string Kind, string? Message);

public enum RefreshActivityState
{
    Idle,
    Refreshing,
    Paused,
    Offline,
    Error
}

public enum NotificationKind
{
    NewStoriesSummary,
    SourceFailedRepeatedly,
    FirstRunCompleted,
    BackgroundRefreshFailed,
    AppUpdateAvailable
}

public sealed record NotificationAction(string Id, string Label, string? StoryId = null, string? SourceId = null);

public sealed record NotificationPayload(
    NotificationKind Kind,
    string Title,
    string Body,
    IReadOnlyList<Story> Stories,
    IReadOnlyList<NotificationAction> Actions,
    DateTimeOffset CreatedAt);
