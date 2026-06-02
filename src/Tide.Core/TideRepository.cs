using System.Net.Http.Headers;
using System.Text.Json;

namespace Tide.Core;

public sealed class TideRepository
{
    private const int MaxStories = 500;
    private static readonly string[] Accents =
        ["#D98368", "#698C7A", "#7D83B2", "#C59550", "#9D79A3", "#568DA0"];

    private readonly HttpClient _httpClient;
    private readonly string _storagePath;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public TideRepository(string? storagePath = null, HttpClient? httpClient = null)
    {
        if (storagePath is null)
        {
            PortablePaths.MigrateLegacyData();
        }
        _storagePath = storagePath ?? PortablePaths.StoragePath;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(18) };
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Tide", "0.1"));
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd(
            "text/html, application/rss+xml, application/atom+xml, application/xml;q=0.9");
    }

    public async Task<Snapshot> LoadAsync()
    {
        if (!File.Exists(_storagePath))
        {
            return new Snapshot();
        }

        try
        {
            await using var stream = File.OpenRead(_storagePath);
            return await JsonSerializer.DeserializeAsync<Snapshot>(stream, _jsonOptions) ?? new Snapshot();
        }
        catch
        {
            return new Snapshot();
        }
    }

    public async Task<Snapshot> AddSourceAsync(Snapshot snapshot, string rawUrl)
    {
        var fetched = await FetchSourceAsync(rawUrl, Accents[snapshot.Sources.Count % Accents.Length]);
        if (snapshot.Sources.Any(source => source.Id == fetched.Source.Id))
        {
            throw new InvalidOperationException("このサイトはすでに登録されています。");
        }

        return await SaveAsync(snapshot with
        {
            Sources = [.. snapshot.Sources, fetched.Source],
            Stories = MergeStories(snapshot.Stories, fetched.Stories),
            LastRefreshedAt = DateTimeOffset.UtcNow
        });
    }

    public async Task<RefreshResult> RefreshAsync(Snapshot snapshot)
    {
        var failures = new List<string>();
        var sources = snapshot.Sources.ToList();
        var stories = snapshot.Stories;
        for (var index = 0; index < snapshot.Sources.Count; index++)
        {
            var previous = snapshot.Sources[index];
            try
            {
                var fetched = await FetchSourceAsync(previous.Url, previous.Accent);
                sources[index] = fetched.Source with { AddedAt = previous.AddedAt };
                stories = MergeStories(stories, fetched.Stories);
            }
            catch
            {
                failures.Add(previous.Title);
            }
        }

        var updated = await SaveAsync(snapshot with
        {
            Sources = sources,
            Stories = stories,
            LastRefreshedAt = DateTimeOffset.UtcNow
        });
        return new RefreshResult(updated, failures);
    }

    public Task<Snapshot> RemoveSourceAsync(Snapshot snapshot, string sourceId) =>
        SaveAsync(snapshot with
        {
            Sources = snapshot.Sources.Where(source => source.Id != sourceId).ToList(),
            Stories = snapshot.Stories.Where(story => story.SourceId != sourceId).ToList()
        });

    public Task<Snapshot> MarkReadAsync(Snapshot snapshot, string storyId) =>
        SaveAsync(snapshot with
        {
            Stories = snapshot.Stories
                .Select(story => story.Id == storyId ? story with { IsRead = true } : story)
                .ToList()
        });

    public Task<Snapshot> MarkAllReadAsync(Snapshot snapshot) =>
        SaveAsync(snapshot with
        {
            Stories = snapshot.Stories.Select(story => story with { IsRead = true }).ToList()
        });

    public Task<Snapshot> ToggleSavedAsync(Snapshot snapshot, string storyId) =>
        SaveAsync(snapshot with
        {
            Stories = snapshot.Stories
                .Select(story => story.Id == storyId ? story with { IsSaved = !story.IsSaved } : story)
                .ToList()
        });

    private async Task<FetchedSource> FetchSourceAsync(string rawUrl, string accent)
    {
        var pageUri = FeedParser.NormalizeUrl(rawUrl);
        var sourceId = FeedParser.SourceIdFor(pageUri);
        var body = await DownloadAsync(pageUri);
        var directFeed = FeedParser.ParseFeed(body, sourceId, pageUri);
        if (directFeed is not null)
        {
            return Feed(sourceId, pageUri, pageUri, directFeed, accent, null);
        }

        var iconUrl = FeedParser.PageIcon(body, pageUri);
        foreach (var candidate in FeedParser.FindFeedCandidates(body, pageUri))
        {
            try
            {
                var parsed = FeedParser.ParseFeed(await DownloadAsync(candidate), sourceId, candidate);
                if (parsed is not null)
                {
                    return Feed(sourceId, pageUri, candidate, parsed, accent, iconUrl);
                }
            }
            catch
            {
                // Feed discovery is best-effort. The page itself remains a useful source.
            }
        }

        return new FetchedSource(
            new Source
            {
                Id = sourceId,
                Title = FeedParser.PageTitle(body, pageUri.Host),
                Url = pageUri.ToString(),
                Kind = "website",
                IconUrl = iconUrl,
                Accent = accent,
                AddedAt = DateTimeOffset.UtcNow
            },
            FeedParser.ParseWebsite(body, sourceId, pageUri));
    }

    private async Task<string> DownloadAsync(Uri uri)
    {
        using var response = await _httpClient.GetAsync(uri);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private static FetchedSource Feed(string sourceId, Uri pageUri, Uri feedUri, ParsedFeed feed,
        string accent, string? iconUrl) =>
        new(
            new Source
            {
                Id = sourceId,
                Title = string.IsNullOrWhiteSpace(feed.Title) ? pageUri.Host : feed.Title,
                Url = pageUri.ToString(),
                FeedUrl = feedUri.ToString(),
                Kind = "feed",
                IconUrl = iconUrl,
                Accent = accent,
                AddedAt = DateTimeOffset.UtcNow
            },
            feed.Stories);

    private static List<Story> MergeStories(IEnumerable<Story> existing, IEnumerable<Story> incoming)
    {
        var stories = existing.ToDictionary(story => story.Id);
        foreach (var story in incoming)
        {
            stories[story.Id] = stories.TryGetValue(story.Id, out var previous)
                ? story with { IsRead = previous.IsRead, IsSaved = previous.IsSaved }
                : story;
        }

        return stories.Values
            .OrderByDescending(story => story.PublishedAt)
            .Take(MaxStories)
            .ToList();
    }

    private async Task<Snapshot> SaveAsync(Snapshot snapshot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_storagePath)!);
        await using var stream = File.Create(_storagePath);
        await JsonSerializer.SerializeAsync(stream, snapshot, _jsonOptions);
        return snapshot;
    }

    private sealed record FetchedSource(Source Source, IReadOnlyList<Story> Stories);
}
