namespace Tide.Core;

public static class NotificationPlanner
{
    public static NotificationPayload? BuildNewStoriesPayload(
        Snapshot snapshot,
        IReadOnlyList<Story> newStories,
        DateTimeOffset? now = null,
        bool manualRefresh = false)
    {
        var current = now ?? DateTimeOffset.Now;
        var settings = snapshot.Settings.Normalize();
        if (!settings.NotificationsEnabled)
        {
            return null;
        }

        if (manualRefresh && !settings.NotifyOnManualRefresh)
        {
            return null;
        }

        if (settings.PauseNotificationsUntil is not null && settings.PauseNotificationsUntil > current)
        {
            return null;
        }

        if (settings.QuietHoursEnabled && IsQuietHour(current, settings.QuietHoursStart, settings.QuietHoursEnd))
        {
            return null;
        }

        var sources = snapshot.Sources.ToDictionary(source => source.Id);
        var targets = newStories
            .Where(story => !story.IsInitial)
            .Where(story => sources.TryGetValue(story.SourceId, out var source) &&
                            source is { IsEnabled: true, NotificationsEnabled: true })
            .OrderByDescending(story => story.PublishedAt)
            .Take(12)
            .ToArray();
        if (targets.Length == 0)
        {
            return null;
        }

        var sourceNames = targets
            .Select(story => sources[story.SourceId].Title)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToArray();
        var sourceText = string.Join("、", sourceNames);
        if (targets.Select(story => story.SourceId).Distinct().Count() > sourceNames.Length)
        {
            sourceText += " など";
        }

        var title = targets.Length == 1 ? "新着があります" : $"{targets.Length}件の新着があります";
        var body = string.IsNullOrWhiteSpace(sourceText)
            ? "Tideで確認"
            : $"{sourceText} に新着。Tideで確認";

        return new NotificationPayload(
            NotificationKind.NewStoriesSummary,
            title,
            body,
            targets,
            [
                new NotificationAction("openApp", "Tideで確認"),
                new NotificationAction("markAllRead", "すべて既読にする"),
                new NotificationAction("pause1h", "通知を1時間停止")
            ],
            current);
    }

    public static bool IsQuietHour(DateTimeOffset now, string start, string end)
    {
        if (!TimeOnly.TryParse(start, out var startTime) || !TimeOnly.TryParse(end, out var endTime))
        {
            return false;
        }

        var current = TimeOnly.FromDateTime(now.LocalDateTime);
        return startTime <= endTime
            ? current >= startTime && current < endTime
            : current >= startTime || current < endTime;
    }
}
