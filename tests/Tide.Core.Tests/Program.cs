using Tide.Core;

var checks = new (string Name, Action Check)[]
{
    ("normalizes bare URL", () =>
        Equal("https://example.com/news", FeedParser.NormalizeUrl("example.com/news").ToString().TrimEnd('/'))),
    ("uses portable storage beside executable", () =>
        Equal(Path.Combine(AppContext.BaseDirectory, "Data", "watcher-data.json"), PortablePaths.StoragePath)),
    ("parses RSS", () =>
    {
        var feed = FeedParser.ParseFeed(
            "<rss><channel><title>Journal</title><item><title>A first note</title><link>/notes/1</link><description><![CDATA[<p>Quiet details.</p>]]></description></item></channel></rss>",
            "journal",
            new Uri("https://example.com/feed.xml"));
        Equal("Journal", feed?.Title);
        Equal("A first note", feed?.Stories[0].Title);
        Equal("Quiet details.", feed?.Stories[0].Summary);
        Equal("https://example.com/notes/1", feed?.Stories[0].Url);
    }),
    ("parses Atom", () =>
    {
        var feed = FeedParser.ParseFeed(
            "<feed><title>Notes</title><entry><title>New workspace</title><link href=\"/posts/2\" /><summary>Small improvements.</summary><updated>2026-01-02T03:04:05Z</updated></entry></feed>",
            "notes",
            new Uri("https://example.com/atom.xml"));
        Equal("New workspace", feed?.Stories[0].Title);
        Equal("https://example.com/posts/2", feed?.Stories[0].Url);
    }),
    ("discovers feeds", () =>
    {
        const string html = "<html><head><link rel=\"alternate\" type=\"application/rss+xml\" href=\"/feed.xml\"></head></html>";
        Equal("https://example.com/feed.xml", FeedParser.FindFeedCandidates(html, new Uri("https://example.com"))[0].ToString());
    }),
    ("extracts article cards", () =>
    {
        const string html = "<article><h2><a href=\"/journal/calm\">A thoughtful first release</a></h2><p>Gentle details.</p></article>";
        var story = FeedParser.ParseWebsite(html, "site", new Uri("https://example.com"))[0];
        Equal("A thoughtful first release", story.Title);
        Equal("Gentle details.", story.Summary);
        Equal("https://example.com/journal/calm", story.Url);
    }),
    ("strips markup", () => Equal("Hello calm web.", FeedParser.StripMarkup("<p>Hello <strong>calm</strong> web.</p>")))
};

foreach (var (name, check) in checks)
{
    check();
    Console.WriteLine($"PASS {name}");
}

Console.WriteLine($"Verified {checks.Length} Tide.Core behaviors.");
return;

static void Equal(string? expected, string? actual)
{
    if (!string.Equals(expected, actual, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"Expected '{expected}', received '{actual}'.");
    }
}
