using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Tide.Core;

public sealed class TideRepository
{
    private const int MaxResponseBytes = 4 * 1024 * 1024;
    private static readonly string[] Accents =
        ["#D98368", "#698C7A", "#7D83B2", "#C59550", "#9D79A3", "#568DA0"];

    private readonly HttpClient _httpClient;
    private readonly StorageService _storage;
    private readonly TideLogger _logger;

    public TideRepository(string? storagePath = null, HttpClient? httpClient = null, TideLogger? logger = null)
    {
        if (storagePath is null)
        {
            PortablePaths.MigrateLegacyData();
        }

        _logger = logger ?? new TideLogger();
        _storage = new StorageService(storagePath, _logger);
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(18) };
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Tide", "0.2"));
        }

        if (!_httpClient.DefaultRequestHeaders.Accept.Any())
        {
            _httpClient.DefaultRequestHeaders.Accept.ParseAdd(
                "text/html, application/rss+xml, application/atom+xml, application/xml;q=0.9, text/xml;q=0.8");
        }
    }

    public string StoragePath => _storage.StoragePath;

    public async Task<Snapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await _storage.LoadAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(snapshot.DataWarning))
        {
            _logger.Warn(snapshot.DataWarning);
        }

        return snapshot;
    }

    public async Task<SourcePreview> PreviewSourceAsync(string rawUrl, CancellationToken cancellationToken = default)
    {
        var fetched = await FetchNewSourceAsync(rawUrl, Accents[0], cancellationToken);
        return new SourcePreview(
            fetched.Source,
            fetched.Stories,
            fetched.Source.Kind,
            fetched.Stories.Count == 0 ? "記事候補はまだ見つかっていません。" : null);
    }

    public async Task<Snapshot> AddSourceAsync(Snapshot snapshot, string rawUrl, CancellationToken cancellationToken = default)
    {
        var fetched = await FetchNewSourceAsync(rawUrl, Accents[snapshot.Sources.Count % Accents.Length], cancellationToken);
        if (snapshot.Sources.Any(source =>
                source.Id == fetched.Source.Id ||
                string.Equals(source.Url, fetched.Source.Url, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(source.FeedUrl) &&
                 string.Equals(source.FeedUrl, fetched.Source.FeedUrl, StringComparison.OrdinalIgnoreCase))))
        {
            throw new InvalidOperationException("このサイトはすでに登録されています。");
        }

        var initialStories = fetched.Stories.Select(story => story with { IsInitial = true }).ToArray();
        var updated = await SaveAsync(snapshot with
        {
            Sources = [.. snapshot.Sources, fetched.Source],
            Stories = MergeStories(snapshot.Stories, initialStories, snapshot.Settings.MaxStories),
            LastRefreshedAt = DateTimeOffset.UtcNow
        }, cancellationToken);
        _logger.Info($"Source added: {fetched.Source.Title} ({fetched.Source.Kind})");
        return updated;
    }

    public async Task<RefreshResult> RefreshAsync(
        Snapshot snapshot,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        var failures = new List<FailedSource>();
        var newStories = new List<Story>();
        var sources = snapshot.Sources.ToList();
        var stories = snapshot.Stories;
        var checkedCount = 0;

        _logger.Info("Refresh started.");
        for (var index = 0; index < sources.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var previous = sources[index];
            if (!previous.IsEnabled)
            {
                continue;
            }

            if (!force && !IsDue(previous, snapshot.Settings, started))
            {
                continue;
            }

            checkedCount++;
            try
            {
                var fetched = await FetchExistingSourceAsync(previous, cancellationToken);
                sources[index] = MergeSourceMetadata(previous, fetched.Source, started, null);

                var knownIds = stories.Select(story => story.Id).ToHashSet(StringComparer.Ordinal);
                var knownUrls = stories
                    .Select(story => story.CanonicalUrl ?? story.Url)
                    .Where(url => !string.IsNullOrWhiteSpace(url))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var fresh = fetched.Stories
                    .Where(story => !knownIds.Contains(story.Id) &&
                                    !knownUrls.Contains(story.CanonicalUrl ?? story.Url))
                    .ToArray();
                stories = MergeStories(stories, fetched.Stories, snapshot.Settings.MaxStories);
                newStories.AddRange(fresh);
                _logger.Info($"Source refreshed: {previous.Title}, new={fresh.Length}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                sources[index] = previous with
                {
                    LastCheckedAt = started,
                    LastError = FriendlyError(exception),
                    ConsecutiveFailureCount = previous.ConsecutiveFailureCount + 1
                };
                failures.Add(new FailedSource(previous.Id, previous.Title, FriendlyError(exception)));
                _logger.Warn($"Source refresh failed: {previous.Title}", exception);
            }
        }

        var finished = DateTimeOffset.UtcNow;
        var updated = await SaveAsync(snapshot with
        {
            Sources = sources,
            Stories = stories,
            LastRefreshedAt = finished
        }, cancellationToken);

        _logger.Info($"Refresh finished. checked={checkedCount}, new={newStories.Count}, failed={failures.Count}");
        return new RefreshResult(updated, newStories, failures, checkedCount, started, finished, false);
    }

    public async Task<RefreshResult> RefreshSourceAsync(
        Snapshot snapshot,
        string sourceId,
        CancellationToken cancellationToken = default)
    {
        var scoped = snapshot with
        {
            Sources = snapshot.Sources
                .Select(source => source.Id == sourceId ? source : source with { IsEnabled = false })
                .ToList()
        };
        var result = await RefreshAsync(scoped, force: true, cancellationToken);
        var resultSources = result.Snapshot.Sources.ToDictionary(source => source.Id);
        var finalSnapshot = await SaveAsync(result.Snapshot with
        {
            Sources = snapshot.Sources
                .Select(source => resultSources.TryGetValue(source.Id, out var updated) ? updated with { IsEnabled = source.IsEnabled } : source)
                .ToList()
        }, cancellationToken);
        return result with { Snapshot = finalSnapshot };
    }

    public Task<Snapshot> RemoveSourceAsync(Snapshot snapshot, string sourceId, CancellationToken cancellationToken = default) =>
        SaveAsync(snapshot with
        {
            Sources = snapshot.Sources.Where(source => source.Id != sourceId).ToList(),
            Stories = snapshot.Stories.Where(story => story.SourceId != sourceId).ToList()
        }, cancellationToken);

    public Task<Snapshot> MarkReadAsync(Snapshot snapshot, string storyId, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        return SaveAsync(snapshot with
        {
            Stories = snapshot.Stories
                .Select(story => story.Id == storyId ? story with { IsRead = true, ReadAt = story.ReadAt ?? now } : story)
                .ToList()
        }, cancellationToken);
    }

    public Task<Snapshot> ToggleReadAsync(Snapshot snapshot, string storyId, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        return SaveAsync(snapshot with
        {
            Stories = snapshot.Stories
                .Select(story => story.Id == storyId
                    ? story with { IsRead = !story.IsRead, ReadAt = story.IsRead ? null : now }
                    : story)
                .ToList()
        }, cancellationToken);
    }

    public Task<Snapshot> MarkAllReadAsync(Snapshot snapshot, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        return SaveAsync(snapshot with
        {
            Stories = snapshot.Stories.Select(story => story with { IsRead = true, ReadAt = story.ReadAt ?? now }).ToList()
        }, cancellationToken);
    }

    public Task<Snapshot> ToggleSavedAsync(Snapshot snapshot, string storyId, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        return SaveAsync(snapshot with
        {
            Stories = snapshot.Stories
                .Select(story => story.Id == storyId
                    ? story with { IsSaved = !story.IsSaved, SavedAt = story.IsSaved ? null : now }
                    : story)
                .ToList()
        }, cancellationToken);
    }

    public Task<Snapshot> MarkStoriesNotifiedAsync(Snapshot snapshot, IEnumerable<string> storyIds, CancellationToken cancellationToken = default)
    {
        var ids = storyIds.ToHashSet(StringComparer.Ordinal);
        var now = DateTimeOffset.UtcNow;
        return SaveAsync(snapshot with
        {
            Stories = snapshot.Stories
                .Select(story => ids.Contains(story.Id)
                    ? story with { WasNotified = true, NotifiedAt = story.NotifiedAt ?? now }
                    : story)
                .ToList()
        }, cancellationToken);
    }

    public Task<Snapshot> SaveSettingsAsync(Snapshot snapshot, AppSettings settings, CancellationToken cancellationToken = default) =>
        SaveAsync(snapshot with { Settings = settings.Normalize() }, cancellationToken);

    public Task<Snapshot> UpdateSourceAsync(
        Snapshot snapshot,
        string sourceId,
        Func<Source, Source> update,
        CancellationToken cancellationToken = default) =>
        SaveAsync(snapshot with
        {
            Sources = snapshot.Sources
                .Select(source => source.Id == sourceId ? update(source) : source)
                .ToList()
        }, cancellationToken);

    public Task ExportAsync(string destinationPath, Snapshot snapshot, CancellationToken cancellationToken = default) =>
        _storage.ExportAsync(destinationPath, snapshot, cancellationToken);

    public Task<Snapshot> ImportAsync(string sourcePath, CancellationToken cancellationToken = default) =>
        _storage.ImportAsync(sourcePath, cancellationToken);

    public Task<Snapshot> SaveAsync(Snapshot snapshot, CancellationToken cancellationToken = default) =>
        _storage.SaveAsync(snapshot, cancellationToken);

    public static List<Story> MergeStories(IEnumerable<Story> existing, IEnumerable<Story> incoming, int maxStories = 500)
    {
        var stories = existing.ToDictionary(story => story.Id);
        var byUrl = existing
            .Where(story => !string.IsNullOrWhiteSpace(story.CanonicalUrl ?? story.Url))
            .GroupBy(story => $"{story.SourceId}:{story.CanonicalUrl ?? story.Url}", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var story in incoming)
        {
            var key = $"{story.SourceId}:{story.CanonicalUrl ?? story.Url}";
            var previous = stories.GetValueOrDefault(story.Id) ??
                           byUrl.GetValueOrDefault(key);
            stories[previous?.Id ?? story.Id] = previous is null
                ? story
                : story with
                {
                    Id = previous.Id,
                    IsRead = previous.IsRead,
                    IsSaved = previous.IsSaved,
                    ReadAt = previous.ReadAt,
                    SavedAt = previous.SavedAt,
                    WasNotified = previous.WasNotified,
                    NotifiedAt = previous.NotifiedAt,
                    IsInitial = previous.IsInitial
                };
        }

        var ordered = stories.Values
            .OrderByDescending(story => story.PublishedAt)
            .ToList();
        var saved = ordered.Where(story => story.IsSaved).ToList();
        var unsaved = ordered
            .Where(story => !story.IsSaved)
            .Take(Math.Max(0, maxStories - saved.Count));
        return saved
            .Concat(unsaved)
            .OrderByDescending(story => story.PublishedAt)
            .ToList();
    }

    private async Task<FetchedSource> FetchNewSourceAsync(string rawUrl, string accent, CancellationToken cancellationToken)
    {
        var pageUri = FeedParser.NormalizeUrl(rawUrl);
        var sourceId = FeedParser.SourceIdFor(pageUri);
        var downloaded = await DownloadAsync(pageUri, null, cancellationToken);
        var directFeed = FeedParser.ParseFeed(downloaded.Body, sourceId, pageUri);
        if (directFeed is not null)
        {
            return Feed(sourceId, pageUri, pageUri, directFeed, accent, null, downloaded);
        }

        var iconUrl = FeedParser.PageIcon(downloaded.Body, pageUri);
        foreach (var candidate in FeedParser.FindFeedCandidates(downloaded.Body, pageUri))
        {
            try
            {
                var feedContent = await DownloadAsync(candidate, null, cancellationToken);
                var parsed = FeedParser.ParseFeed(feedContent.Body, sourceId, candidate);
                if (parsed is not null)
                {
                    return Feed(sourceId, pageUri, candidate, parsed, accent, iconUrl, feedContent);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.Warn($"Feed autodiscovery candidate failed: {candidate.Host}", exception);
            }
        }

        return Website(sourceId, pageUri, downloaded.Body, accent, iconUrl, downloaded);
    }

    private async Task<FetchedSource> FetchExistingSourceAsync(Source previous, CancellationToken cancellationToken)
    {
        var pageUri = FeedParser.NormalizeUrl(previous.Url);
        if (!string.IsNullOrWhiteSpace(previous.FeedUrl))
        {
            var feedUri = FeedParser.NormalizeUrl(previous.FeedUrl);
            var downloaded = await DownloadAsync(feedUri, previous, cancellationToken);
            if (downloaded.NotModified)
            {
                return NotModified(previous, downloaded);
            }

            var parsed = FeedParser.ParseFeed(downloaded.Body, previous.Id, feedUri)
                         ?? throw new InvalidOperationException("RSS/Atomとして解析できませんでした。");
            return Feed(previous.Id, pageUri, feedUri, parsed, previous.Accent, previous.IconUrl, downloaded);
        }

        var page = await DownloadAsync(pageUri, previous, cancellationToken);
        if (page.NotModified)
        {
            return NotModified(previous, page);
        }

        var directFeed = FeedParser.ParseFeed(page.Body, previous.Id, pageUri);
        if (directFeed is not null)
        {
            return Feed(previous.Id, pageUri, pageUri, directFeed, previous.Accent, previous.IconUrl, page);
        }

        var iconUrl = FeedParser.PageIcon(page.Body, pageUri) ?? previous.IconUrl;
        foreach (var candidate in FeedParser.FindFeedCandidates(page.Body, pageUri))
        {
            try
            {
                var feedContent = await DownloadAsync(candidate, null, cancellationToken);
                var parsed = FeedParser.ParseFeed(feedContent.Body, previous.Id, candidate);
                if (parsed is not null)
                {
                    return Feed(previous.Id, pageUri, candidate, parsed, previous.Accent, iconUrl, feedContent);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.Warn($"Feed autodiscovery candidate failed: {candidate.Host}", exception);
            }
        }

        return Website(previous.Id, pageUri, page.Body, previous.Accent, iconUrl, page);
    }

    private async Task<DownloadResult> DownloadAsync(Uri uri, Source? source, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (!string.IsNullOrWhiteSpace(source?.ETag))
        {
            try
            {
                request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(source.ETag));
            }
            catch
            {
                // Ignore malformed values from older data.
            }
        }

        if (!string.IsNullOrWhiteSpace(source?.LastModified) &&
            DateTimeOffset.TryParse(source.LastModified, out var lastModified))
        {
            request.Headers.IfModifiedSince = lastModified;
        }

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return new DownloadResult(string.Empty, true, source?.ETag, source?.LastModified);
        }

        response.EnsureSuccessStatusCode();
        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (!IsSupportedContentType(contentType))
        {
            throw new InvalidOperationException($"対応していないContent-Typeです: {contentType}");
        }

        if (response.Content.Headers.ContentLength > MaxResponseBytes)
        {
            throw new InvalidOperationException("レスポンスが大きすぎます。");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (memory.Length + read > MaxResponseBytes)
            {
                throw new InvalidOperationException("レスポンスが大きすぎます。");
            }

            memory.Write(buffer, 0, read);
        }

        var bytes = memory.ToArray();
        var body = Decode(bytes, response.Content.Headers.ContentType?.CharSet);
        return new DownloadResult(
            body,
            false,
            response.Headers.ETag?.ToString(),
            response.Content.Headers.LastModified?.ToString("R") ?? HeaderValue(response, "Last-Modified"));
    }

    private static FetchedSource Feed(
        string sourceId,
        Uri pageUri,
        Uri feedUri,
        ParsedFeed feed,
        string accent,
        string? iconUrl,
        DownloadResult download) =>
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
                AddedAt = DateTimeOffset.UtcNow,
                ETag = download.ETag,
                LastModified = download.LastModified
            },
            feed.Stories);

    private static FetchedSource Website(
        string sourceId,
        Uri pageUri,
        string body,
        string accent,
        string? iconUrl,
        DownloadResult download) =>
        new(
            new Source
            {
                Id = sourceId,
                Title = FeedParser.PageTitle(body, pageUri.Host),
                Url = pageUri.ToString(),
                Kind = "website",
                IconUrl = iconUrl,
                Accent = accent,
                AddedAt = DateTimeOffset.UtcNow,
                ETag = download.ETag,
                LastModified = download.LastModified
            },
            FeedParser.ParseWebsite(body, sourceId, pageUri));

    private static FetchedSource NotModified(Source previous, DownloadResult download) =>
        new(previous with { ETag = download.ETag ?? previous.ETag, LastModified = download.LastModified ?? previous.LastModified }, []);

    private static Source MergeSourceMetadata(Source previous, Source fetched, DateTimeOffset checkedAt, string? error) =>
        fetched with
        {
            Id = previous.Id,
            Title = string.IsNullOrWhiteSpace(previous.Title) ? fetched.Title : previous.Title,
            AddedAt = previous.AddedAt,
            Accent = previous.Accent,
            IsEnabled = previous.IsEnabled,
            NotificationsEnabled = previous.NotificationsEnabled,
            CustomIntervalMinutes = previous.CustomIntervalMinutes,
            LastCheckedAt = checkedAt,
            LastSucceededAt = error is null ? checkedAt : previous.LastSucceededAt,
            LastError = error,
            ConsecutiveFailureCount = error is null ? 0 : previous.ConsecutiveFailureCount + 1,
            ETag = fetched.ETag ?? previous.ETag,
            LastModified = fetched.LastModified ?? previous.LastModified
        };

    private static bool IsDue(Source source, AppSettings settings, DateTimeOffset now)
    {
        var interval = Math.Clamp(source.CustomIntervalMinutes ?? settings.RefreshIntervalMinutes, 5, 1440);
        return source.LastCheckedAt is null || now - source.LastCheckedAt.Value >= TimeSpan.FromMinutes(interval);
    }

    private static bool IsSupportedContentType(string contentType) =>
        string.IsNullOrWhiteSpace(contentType) ||
        contentType.Contains("html", StringComparison.OrdinalIgnoreCase) ||
        contentType.Contains("xml", StringComparison.OrdinalIgnoreCase) ||
        contentType.Contains("rss", StringComparison.OrdinalIgnoreCase) ||
        contentType.Contains("atom", StringComparison.OrdinalIgnoreCase) ||
        contentType.Contains("text/plain", StringComparison.OrdinalIgnoreCase);

    private static string Decode(byte[] bytes, string? charset)
    {
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                return Encoding.GetEncoding(charset.Trim('"')).GetString(bytes);
            }
            catch
            {
                // Fall through to UTF-8 below.
            }
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static string? HeaderValue(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ||
        response.Content.Headers.TryGetValues(name, out values)
            ? values.FirstOrDefault()
            : null;

    private static string FriendlyError(Exception exception) =>
        exception switch
        {
            TaskCanceledException => "タイムアウトしました。",
            HttpRequestException http when http.StatusCode is not null => $"HTTP {(int)http.StatusCode}: {http.StatusCode}",
            HttpRequestException => "ネットワーク取得に失敗しました。",
            InvalidOperationException => exception.Message,
            ArgumentException => exception.Message,
            _ => "更新中に予期しないエラーが発生しました。"
        };

    private sealed record FetchedSource(Source Source, IReadOnlyList<Story> Stories);

    private sealed record DownloadResult(string Body, bool NotModified, string? ETag, string? LastModified);
}
