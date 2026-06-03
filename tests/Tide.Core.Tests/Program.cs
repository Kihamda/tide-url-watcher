using System.Net;
using System.Text;
using Tide.Core;

var checks = new (string Name, Func<Task> Check)[]
{
    ("normalizes bare URL", () =>
    {
        Equal("https://example.com/news", FeedParser.NormalizeUrl("example.com/news").ToString().TrimEnd('/'));
        return Task.CompletedTask;
    }),
    ("rejects invalid schemes", () =>
    {
        Throws<ArgumentException>(() => FeedParser.NormalizeUrl("javascript:alert(1)"));
        return Task.CompletedTask;
    }),
    ("uses portable storage beside executable", () =>
    {
        Equal(Path.Combine(AppContext.BaseDirectory, "Data", "watcher-data.json"), PortablePaths.StoragePath);
        return Task.CompletedTask;
    }),
    ("parses RSS content encoded dc date and images", () =>
    {
        var feed = FeedParser.ParseFeed(
            """
            <rss xmlns:content="http://purl.org/rss/1.0/modules/content/" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:media="http://search.yahoo.com/mrss/">
              <channel><title>Journal</title>
                <item>
                  <title>A first note</title><link>/notes/1</link><guid>note-1</guid>
                  <description>Short.</description><content:encoded><![CDATA[<p>Quiet <strong>details</strong>.</p>]]></content:encoded>
                  <dc:date>2026-01-02T03:04:05Z</dc:date>
                  <enclosure url="/image.jpg" type="image/jpeg" />
                </item>
                <item><title>Media note</title><link>/notes/2</link><media:content url="/media.png" /></item>
              </channel>
            </rss>
            """,
            "journal",
            new Uri("https://example.com/feed.xml"));
        Equal("Journal", feed?.Title);
        Equal("Quiet details.", feed?.Stories[0].Summary);
        Equal("https://example.com/notes/1", feed?.Stories[0].Url);
        Equal("https://example.com/image.jpg", feed?.Stories[0].ImageUrl);
        Equal("https://example.com/media.png", feed?.Stories[1].ImageUrl);
        EqualInt(2026, feed?.Stories[0].PublishedAt.Year);
        return Task.CompletedTask;
    }),
    ("parses Atom alternate link id and media thumbnail", () =>
    {
        var feed = FeedParser.ParseFeed(
            """
            <feed xmlns:media="http://search.yahoo.com/mrss/">
              <title>Notes</title>
              <entry><title>New workspace</title><id>tag:example.com,2026:2</id><link rel="alternate" href="/posts/2" /><summary>Small improvements.</summary><updated>2026-01-02T03:04:05Z</updated><media:thumbnail url="/thumb.jpg" /></entry>
            </feed>
            """,
            "notes",
            new Uri("https://example.com/atom.xml"));
        Equal("New workspace", feed?.Stories[0].Title);
        Equal("https://example.com/posts/2", feed?.Stories[0].Url);
        Equal("https://example.com/thumb.jpg", feed?.Stories[0].ImageUrl);
        return Task.CompletedTask;
    }),
    ("discovers feeds", () =>
    {
        const string html = "<html><head><link rel=\"alternate\" type=\"application/rss+xml\" href=\"/feed.xml\"></head></html>";
        Equal("https://example.com/feed.xml", FeedParser.FindFeedCandidates(html, new Uri("https://example.com"))[0].ToString());
        return Task.CompletedTask;
    }),
    ("extracts article cards", () =>
    {
        const string html = "<article><h2><a href=\"/journal/calm\">A thoughtful first release</a></h2><p>Gentle details.</p></article>";
        var story = FeedParser.ParseWebsite(html, "site", new Uri("https://example.com"))[0];
        Equal("A thoughtful first release", story.Title);
        Equal("Gentle details.", story.Summary);
        Equal("https://example.com/journal/calm", story.Url);
        return Task.CompletedTask;
    }),
    ("extracts JSON-LD article", () =>
    {
        const string html = """
            <script type="application/ld+json">{"@type":"NewsArticle","headline":"A JSON story","url":"/json-story","description":"Structured summary.","datePublished":"2026-03-04","image":{"url":"/json.jpg"}}</script>
            """;
        var story = FeedParser.ParseWebsite(html, "site", new Uri("https://example.com"))[0];
        Equal("A JSON story", story.Title);
        Equal("Structured summary.", story.Summary);
        Equal("https://example.com/json-story", story.Url);
        Equal("https://example.com/json.jpg", story.ImageUrl);
        return Task.CompletedTask;
    }),
    ("uses og fallback", () =>
    {
        const string html = """
            <html><head><meta property="og:title" content="Open graph title"><meta property="og:description" content="Open graph summary"><meta property="og:image" content="/og.png"><link rel="canonical" href="/canonical"></head></html>
            """;
        var story = FeedParser.ParseWebsite(html, "site", new Uri("https://example.com/page"))[0];
        Equal("Open graph title", story.Title);
        Equal("Open graph summary", story.Summary);
        Equal("https://example.com/canonical", story.Url);
        Equal("https://example.com/og.png", story.ImageUrl);
        return Task.CompletedTask;
    }),
    ("parses Japanese dates", () =>
    {
        EqualInt(2026, FeedParser.ParseDate("2026年6月3日")?.Year);
        return Task.CompletedTask;
    }),
    ("strips script style and markup", () =>
    {
        Equal("Hello calm web.", FeedParser.StripMarkup("<style>.x{}</style><script>alert(1)</script><p>Hello <strong>calm</strong> web.</p>"));
        return Task.CompletedTask;
    }),
    ("keeps stable story id when title changes", () =>
    {
        var first = FeedParser.ParseFeed("<rss><channel><item><title>Old title</title><link>/same</link><guid>stable</guid></item></channel></rss>", "s", new Uri("https://example.com/feed"))!;
        var second = FeedParser.ParseFeed("<rss><channel><item><title>New title</title><link>/same</link><guid>stable</guid></item></channel></rss>", "s", new Uri("https://example.com/feed"))!;
        Equal(first.Stories[0].Id, second.Stories[0].Id);
        return Task.CompletedTask;
    }),
    ("merge keeps read and saved state by url fallback", () =>
    {
        var previous = Story("old", "source", "https://example.com/a") with { IsRead = true, IsSaved = true, ReadAt = DateTimeOffset.UtcNow, SavedAt = DateTimeOffset.UtcNow };
        var incoming = Story("new", "source", "https://example.com/a") with { Title = "Changed" };
        var merged = TideRepository.MergeStories([previous], [incoming]);
        Equal("old", merged[0].Id);
        True(merged[0].IsRead);
        True(merged[0].IsSaved);
        return Task.CompletedTask;
    }),
    ("atomic save writes backup and removes tmp", async () =>
    {
        using var directory = TempDirectory();
        var path = Path.Combine(directory.Path, "watcher-data.json");
        var storage = new StorageService(path, new TideLogger(Path.Combine(directory.Path, "app.log")));
        await storage.SaveAsync(new Snapshot { Sources = [Source("s")] });
        True(File.Exists(path));
        True(File.Exists(path + ".bak"));
        True(!File.Exists(path + ".tmp"));
    }),
    ("backup restore recovers corrupt primary", async () =>
    {
        using var directory = TempDirectory();
        var path = Path.Combine(directory.Path, "watcher-data.json");
        var storage = new StorageService(path, new TideLogger(Path.Combine(directory.Path, "app.log")));
        await storage.SaveAsync(new Snapshot { Sources = [Source("backup")] });
        File.WriteAllText(path, "{ broken json");
        var loaded = await storage.LoadAsync();
        Equal("backup", loaded.Sources[0].Id);
        True(loaded.DataWarning?.Contains("バックアップ", StringComparison.Ordinal) == true);
    }),
    ("corrupt json without backup returns empty warning", async () =>
    {
        using var directory = TempDirectory();
        var path = Path.Combine(directory.Path, "watcher-data.json");
        File.WriteAllText(path, "{ broken json");
        var storage = new StorageService(path, new TideLogger(Path.Combine(directory.Path, "app.log")));
        var loaded = await storage.LoadAsync();
        Equal("0", loaded.Sources.Count.ToString());
        True(!string.IsNullOrWhiteSpace(loaded.DataWarning));
    }),
    ("settings default and bounds", () =>
    {
        var settings = new Snapshot { Settings = new AppSettings { RefreshIntervalMinutes = 1, MaxStories = 0 } }.Normalize().Settings;
        Equal("5", settings.RefreshIntervalMinutes.ToString());
        Equal("500", settings.MaxStories.ToString());
        True(settings.AutoRefreshEnabled);
        True(settings.NotificationsEnabled);
        return Task.CompletedTask;
    }),
    ("disabled source is not fetched", async () =>
    {
        using var directory = TempDirectory();
        var handler = new FakeHandler(_ => throw new InvalidOperationException("should not fetch"));
        var repository = new TideRepository(Path.Combine(directory.Path, "watcher-data.json"), new HttpClient(handler), new TideLogger(Path.Combine(directory.Path, "app.log")));
        var snapshot = new Snapshot { Sources = [Source("s") with { IsEnabled = false }] };
        var result = await repository.RefreshAsync(snapshot, force: true);
        Equal("0", handler.Calls.ToString());
        Equal("0", result.CheckedSourcesCount.ToString());
    }),
    ("notification selection skips first run and quiet hours", () =>
    {
        var source = Source("s");
        var initial = Story("i", "s", "https://example.com/i") with { IsInitial = true };
        var normal = Story("n", "s", "https://example.com/n");
        var snapshot = new Snapshot { Sources = [source], Stories = [initial, normal] };
        True(NotificationPlanner.BuildNewStoriesPayload(snapshot, [initial], DateTimeOffset.Now) is null);
        True(NotificationPlanner.BuildNewStoriesPayload(snapshot, [normal], DateTimeOffset.Now) is not null);
        var quiet = snapshot with { Settings = snapshot.Settings with { QuietHoursEnabled = true, QuietHoursStart = "00:00", QuietHoursEnd = "23:59" } };
        True(NotificationPlanner.BuildNewStoriesPayload(quiet, [normal], DateTimeOffset.Now) is null);
        return Task.CompletedTask;
    })
};

foreach (var (name, check) in checks)
{
    await check();
    Console.WriteLine($"PASS {name}");
}

Console.WriteLine($"Verified {checks.Length} Tide.Core behaviors.");
return;

static Story Story(string id, string sourceId, string url) => new()
{
    Id = id,
    SourceId = sourceId,
    Title = "Title",
    Summary = "Summary",
    Url = url,
    CanonicalUrl = url,
    PublishedAt = DateTimeOffset.UtcNow,
    DiscoveredAt = DateTimeOffset.UtcNow
};

static Source Source(string id) => new()
{
    Id = id,
    Title = id,
    Url = "https://example.com",
    Kind = "website",
    Accent = "#53695D",
    AddedAt = DateTimeOffset.UtcNow
};

static TemporaryDirectory TempDirectory() => new(Path.Combine(Path.GetTempPath(), $"tide-tests-{Guid.NewGuid():N}"));

static void Equal(string? expected, string? actual)
{
    if (!string.Equals(expected, actual, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"Expected '{expected}', received '{actual}'.");
    }
}

static void EqualInt(int? expected, int? actual) => Equal(expected?.ToString(), actual?.ToString());

static void True(bool value)
{
    if (!value)
    {
        throw new InvalidOperationException("Expected condition to be true.");
    }
}

static void Throws<TException>(Action action)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory(string path)
    {
        Path = path;
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

sealed class FakeHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

    public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        _responseFactory = responseFactory;
    }

    public int Calls { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(_responseFactory(request));
    }
}
