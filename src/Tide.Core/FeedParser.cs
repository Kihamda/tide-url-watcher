using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Tide.Core;

public static partial class FeedParser
{
    public static ParsedFeed? ParseFeed(string content, string sourceId, Uri feedUri)
    {
        try
        {
            var document = XDocument.Parse(content);
            var root = document.Root;
            if (root is null)
            {
                return null;
            }

            if (root.Name.LocalName.Equals("rss", StringComparison.OrdinalIgnoreCase) ||
                root.Name.LocalName.Equals("rdf", StringComparison.OrdinalIgnoreCase))
            {
                return ParseRss(root, sourceId, feedUri);
            }

            if (root.Name.LocalName.Equals("feed", StringComparison.OrdinalIgnoreCase))
            {
                return ParseAtom(root, sourceId, feedUri);
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    public static IReadOnlyList<Uri> FindFeedCandidates(string html, Uri pageUri)
    {
        var candidates = new List<Uri>();
        foreach (Match match in LinkTagRegex().Matches(html))
        {
            var tag = match.Value;
            var rel = Attribute(tag, "rel");
            var type = Attribute(tag, "type");
            var href = Attribute(tag, "href");
            if (href is not null &&
                rel?.Contains("alternate", StringComparison.OrdinalIgnoreCase) == true &&
                type is not null &&
                (type.Contains("rss", StringComparison.OrdinalIgnoreCase) ||
                 type.Contains("atom", StringComparison.OrdinalIgnoreCase) ||
                 type.Contains("xml", StringComparison.OrdinalIgnoreCase)))
            {
                candidates.Add(Resolve(pageUri, href));
            }
        }

        foreach (var path in new[] { "/feed", "/feed.xml", "/rss.xml", "/atom.xml" })
        {
            candidates.Add(Resolve(pageUri, path));
        }

        return candidates.Distinct().ToArray();
    }

    public static IReadOnlyList<Story> ParseWebsite(string html, string sourceId, Uri pageUri)
    {
        var stories = new List<Story>();
        foreach (Match articleMatch in ArticleRegex().Matches(html))
        {
            var article = articleMatch.Groups[1].Value;
            var link = AnchorRegex().Match(article);
            var title = StripMarkup(link.Groups[2].Value);
            if (!link.Success || title.Length < 8)
            {
                continue;
            }

            var summary = ParagraphRegex().Match(article).Groups[1].Value;
            var image = ImageRegex().Match(article).Groups[1].Value;
            var published = TimeRegex().Match(article).Groups[1].Value;
            stories.Add(CreateStory(
                sourceId,
                title,
                summary,
                Resolve(pageUri, link.Groups[1].Value).ToString(),
                published,
                string.IsNullOrWhiteSpace(image) ? null : Resolve(pageUri, image).ToString()));
        }

        if (stories.Count > 0)
        {
            return stories.Take(40).ToArray();
        }

        return
        [
            CreateStory(
                sourceId,
                PageTitle(html, pageUri.Host),
                MetaContent(html, "description") ?? MetaContent(html, "og:description") ?? string.Empty,
                pageUri.ToString(),
                null,
                MetaContent(html, "og:image"))
        ];
    }

    public static Uri NormalizeUrl(string rawUrl)
    {
        var trimmed = rawUrl.Trim();
        var value = trimmed.Contains("://", StringComparison.Ordinal) ? trimmed : $"https://{trimmed}";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("http または https のURLを入力してください。");
        }

        var builder = new UriBuilder(uri) { Fragment = string.Empty };
        return builder.Uri;
    }

    public static string SourceIdFor(Uri uri) => StableId($"source:{uri}");

    public static string PageTitle(string html, string fallback) =>
        MetaContent(html, "og:site_name") ??
        StripMarkup(TitleRegex().Match(html).Groups[1].Value) switch
        {
            "" => fallback,
            var title => title
        };

    public static string? PageIcon(string html, Uri pageUri)
    {
        foreach (Match match in LinkTagRegex().Matches(html))
        {
            var rel = Attribute(match.Value, "rel");
            var href = Attribute(match.Value, "href");
            if (href is not null && rel?.Contains("icon", StringComparison.OrdinalIgnoreCase) == true)
            {
                return Resolve(pageUri, href).ToString();
            }
        }

        return Resolve(pageUri, "/favicon.ico").ToString();
    }

    public static string StripMarkup(string value) =>
        WebUtility.HtmlDecode(MarkupRegex().Replace(value, " "))
            .Replace('\u00a0', ' ')
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Aggregate(string.Empty, (current, part) => current.Length == 0 ? part : $"{current} {part}");

    private static ParsedFeed ParseRss(XElement root, string sourceId, Uri feedUri)
    {
        var channel = root.Descendants().FirstOrDefault(element => element.Name.LocalName == "channel") ?? root;
        var stories = channel.Elements()
            .Where(element => element.Name.LocalName == "item")
            .Select(item =>
            {
                var title = Value(item, "title");
                var link = Value(item, "link") ?? Value(item, "guid");
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(link))
                {
                    return null;
                }

                var summary = Value(item, "description") ?? Value(item, "encoded") ?? string.Empty;
                var image = item.Elements().FirstOrDefault(element =>
                        element.Name.LocalName is "enclosure" or "content")?
                    .Attribute("url")?.Value ?? ImageFromMarkup(summary, feedUri);
                return CreateStory(sourceId, title, summary, Resolve(feedUri, link).ToString(),
                    Value(item, "pubDate") ?? Value(item, "date"), image);
            })
            .OfType<Story>()
            .Take(80)
            .ToArray();

        return new ParsedFeed(Value(channel, "title"), stories);
    }

    private static ParsedFeed ParseAtom(XElement feed, string sourceId, Uri feedUri)
    {
        var stories = feed.Elements()
            .Where(element => element.Name.LocalName == "entry")
            .Select(entry =>
            {
                var title = Value(entry, "title");
                var linkElement = entry.Elements().FirstOrDefault(element =>
                    element.Name.LocalName == "link" &&
                    (element.Attribute("rel")?.Value is null or "alternate")) ??
                    entry.Elements().FirstOrDefault(element => element.Name.LocalName == "link");
                var link = linkElement?.Attribute("href")?.Value ?? Value(entry, "id");
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(link))
                {
                    return null;
                }

                var summary = Value(entry, "summary") ?? Value(entry, "content") ?? string.Empty;
                return CreateStory(sourceId, title, summary, Resolve(feedUri, link).ToString(),
                    Value(entry, "published") ?? Value(entry, "updated"), ImageFromMarkup(summary, feedUri));
            })
            .OfType<Story>()
            .Take(80)
            .ToArray();

        return new ParsedFeed(Value(feed, "title"), stories);
    }

    private static Story CreateStory(string sourceId, string title, string summary, string url,
        string? published, string? imageUrl)
    {
        var now = DateTimeOffset.UtcNow;
        return new Story
        {
            Id = StableId($"{sourceId}:{url}:{title}"),
            SourceId = sourceId,
            Title = Truncate(StripMarkup(title), 180),
            Summary = Truncate(StripMarkup(summary), 420),
            Url = url,
            ImageUrl = imageUrl,
            PublishedAt = ParseDate(published) ?? now,
            DiscoveredAt = now
        };
    }

    private static string? Value(XElement element, string localName) =>
        element.Elements().FirstOrDefault(child => child.Name.LocalName == localName)?.Value;

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;

    private static Uri Resolve(Uri baseUri, string value) =>
        Uri.TryCreate(baseUri, value, out var uri) ? uri : baseUri;

    private static string StableId(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..18];

    private static string Truncate(string value, int maxLength) =>
        value.Length > maxLength ? $"{value[..maxLength]}..." : value;

    private static string? ImageFromMarkup(string html, Uri baseUri)
    {
        var image = ImageRegex().Match(html).Groups[1].Value;
        return string.IsNullOrWhiteSpace(image) ? null : Resolve(baseUri, image).ToString();
    }

    private static string? MetaContent(string html, string name)
    {
        foreach (Match match in MetaTagRegex().Matches(html))
        {
            var tag = match.Value;
            var key = Attribute(tag, "name") ?? Attribute(tag, "property");
            if (key?.Equals(name, StringComparison.OrdinalIgnoreCase) == true)
            {
                return Attribute(tag, "content");
            }
        }

        return null;
    }

    private static string? Attribute(string tag, string name) =>
        Regex.Match(tag, $"""(?i)\b{Regex.Escape(name)}=["']([^"']+)["']""").Groups[1].Value switch
        {
            "" => null,
            var value => WebUtility.HtmlDecode(value)
        };

    [GeneratedRegex("""<link\b[^>]*>""", RegexOptions.IgnoreCase)]
    private static partial Regex LinkTagRegex();

    [GeneratedRegex("""<meta\b[^>]*>""", RegexOptions.IgnoreCase)]
    private static partial Regex MetaTagRegex();

    [GeneratedRegex("""<article\b[^>]*>([\s\S]*?)</article>""", RegexOptions.IgnoreCase)]
    private static partial Regex ArticleRegex();

    [GeneratedRegex("""<a\b[^>]*\bhref=["']([^"']+)["'][^>]*>([\s\S]*?)</a>""", RegexOptions.IgnoreCase)]
    private static partial Regex AnchorRegex();

    [GeneratedRegex("""<p\b[^>]*>([\s\S]*?)</p>""", RegexOptions.IgnoreCase)]
    private static partial Regex ParagraphRegex();

    [GeneratedRegex("""<img\b[^>]*\b(?:src|data-src)=["']([^"']+)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex ImageRegex();

    [GeneratedRegex("""<time\b[^>]*\bdatetime=["']([^"']+)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex TimeRegex();

    [GeneratedRegex("""<title\b[^>]*>([\s\S]*?)</title>""", RegexOptions.IgnoreCase)]
    private static partial Regex TitleRegex();

    [GeneratedRegex("""<[^>]+>""")]
    private static partial Regex MarkupRegex();
}
