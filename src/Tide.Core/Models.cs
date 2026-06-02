namespace Tide.Core;

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
}

public sealed record Snapshot
{
    public List<Source> Sources { get; init; } = [];
    public List<Story> Stories { get; init; } = [];
    public DateTimeOffset? LastRefreshedAt { get; init; }
}

public sealed record RefreshResult(Snapshot Snapshot, IReadOnlyList<string> FailedSources);

public sealed record ParsedFeed(string? Title, IReadOnlyList<Story> Stories);
