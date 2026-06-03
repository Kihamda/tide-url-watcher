using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Tide.Core;

public static partial class FeedParser
{
    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd",
        "yyyy/MM/dd",
        "yyyy年M月d日",
        "yyyy年MM月dd日"
    ];

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
                 type.Contains("xml", StringComparison.OrdinalIgnoreCase)) &&
                TryResolve(pageUri, href, out var feedUri))
            {
                candidates.Add(feedUri);
            }
        }

        foreach (var path in new[] { "/feed", "/feed.xml", "/rss.xml", "/atom.xml" })
        {
            if (TryResolve(pageUri, path, out var candidate))
            {
                candidates.Add(candidate);
            }
        }

        return candidates.Distinct().ToArray();
    }

    public static IReadOnlyList<Story> ParseWebsite(string html, string sourceId, Uri pageUri)
    {
        var stories = new List<Story>();
        stories.AddRange(ParseJsonLdStories(html, sourceId, pageUri));

        foreach (Match articleMatch in ArticleRegex().Matches(html))
        {
            AddHtmlCandidateStories(stories, articleMatch.Groups[1].Value, sourceId, pageUri);
        }

        foreach (Match mainMatch in MainRegex().Matches(html))
        {
            AddHtmlCandidateStories(stories, mainMatch.Groups[1].Value, sourceId, pageUri);
        }

        if (stories.Count > 0)
        {
            return DeduplicateStories(stories).Take(40).ToArray();
        }

        var title = MetaContent(html, "og:title") ?? PageTitle(html, pageUri.Host);
        var canonical = CanonicalUrl(html, pageUri) ?? pageUri.ToString();
        return
        [
            CreateStory(
                sourceId,
                title,
                MetaContent(html, "description") ?? MetaContent(html, "og:description") ?? string.Empty,
                canonical,
                null,
                ResolveOptional(pageUri, MetaContent(html, "og:image")),
                canonical)
        ];
    }

    public static Uri NormalizeUrl(string rawUrl)
    {
        var trimmed = rawUrl.Trim();
        var value = trimmed.StartsWith("//", StringComparison.Ordinal)
            ? $"https:{trimmed}"
            : trimmed.Contains("://", StringComparison.Ordinal)
                ? trimmed
                : $"https://{trimmed}";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("http または https のURLを入力してください。");
        }

        var builder = new UriBuilder(uri)
        {
            Fragment = string.Empty,
            Scheme = uri.Scheme.ToLowerInvariant(),
            Host = uri.Host.ToLowerInvariant()
        };
        return builder.Uri;
    }

    public static string SourceIdFor(Uri uri) => StableId($"source:{NormalizeUrl(uri.ToString())}");

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
            if (href is not null &&
                rel?.Contains("icon", StringComparison.OrdinalIgnoreCase) == true &&
                TryResolve(pageUri, href, out var icon))
            {
                return icon.ToString();
            }
        }

        return Resolve(pageUri, "/favicon.ico").ToString();
    }

    public static string StripMarkup(string value)
    {
        var withoutScripts = ScriptOrStyleRegex().Replace(value, " ");
        var text = WebUtility.HtmlDecode(MarkupRegex().Replace(withoutScripts, " "))
            .Replace('\u00a0', ' ')
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Aggregate(string.Empty, (current, part) => current.Length == 0 ? part : $"{current} {part}");
        return SpaceBeforePunctuationRegex().Replace(text, "$1");
    }

    public static DateTimeOffset? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = StripMarkup(value.Trim());
        if (DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ||
            DateTimeOffset.TryParse(trimmed, CultureInfo.GetCultureInfo("ja-JP"), DateTimeStyles.AssumeLocal, out parsed))
        {
            return parsed.ToUniversalTime();
        }

        foreach (var format in DateFormats)
        {
            if (DateOnly.TryParseExact(trimmed, format, CultureInfo.GetCultureInfo("ja-JP"), DateTimeStyles.None, out var date))
            {
                return new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.Now));
            }
        }

        return null;
    }

    public static string StableStoryId(string sourceId, string? externalId, string? canonicalUrl, string? title)
    {
        var key = FirstNonEmpty(externalId, canonicalUrl, title) ?? Guid.NewGuid().ToString("N");
        return StableId($"story:{sourceId}:{key}");
    }

    public static string NormalizeStoryUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url.Trim();
        }

        var builder = new UriBuilder(uri)
        {
            Fragment = string.Empty,
            Scheme = uri.Scheme.ToLowerInvariant(),
            Host = uri.Host.ToLowerInvariant()
        };
        return builder.Uri.ToString();
    }

    private static ParsedFeed ParseRss(XElement root, string sourceId, Uri feedUri)
    {
        var channel = root.Descendants().FirstOrDefault(element => element.Name.LocalName == "channel") ?? root;
        var stories = channel.Elements()
            .Where(element => element.Name.LocalName == "item")
            .Select(item =>
            {
                var title = Value(item, "title");
                var guid = Value(item, "guid");
                var link = Value(item, "link");
                var rawUrl = FirstNonEmpty(link, guid);
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(rawUrl) ||
                    !TryResolve(feedUri, rawUrl, out var storyUri))
                {
                    return null;
                }

                var summary = Value(item, "description") ?? Value(item, "encoded") ?? string.Empty;
                var content = Value(item, "encoded") ?? summary;
                var image = ImageFromFeedItem(item, content, feedUri);
                return CreateStory(sourceId, title, content, storyUri.ToString(),
                    Value(item, "pubDate") ?? Value(item, "date"),
                    image,
                    guid,
                    storyUri.ToString());
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
                var link = linkElement?.Attribute("href")?.Value;
                var id = Value(entry, "id");
                var rawUrl = FirstNonEmpty(link, id);
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(rawUrl) ||
                    !TryResolve(feedUri, rawUrl, out var storyUri))
                {
                    return null;
                }

                var summary = Value(entry, "summary") ?? Value(entry, "content") ?? string.Empty;
                return CreateStory(sourceId, title, summary, storyUri.ToString(),
                    Value(entry, "published") ?? Value(entry, "updated"),
                    ImageFromFeedItem(entry, summary, feedUri),
                    id,
                    storyUri.ToString());
            })
            .OfType<Story>()
            .Take(80)
            .ToArray();

        return new ParsedFeed(Value(feed, "title"), stories);
    }

    private static IEnumerable<Story> ParseJsonLdStories(string html, string sourceId, Uri pageUri)
    {
        foreach (Match match in JsonLdRegex().Matches(html))
        {
            using var document = TryParseJson(match.Groups[1].Value);
            if (document is null)
            {
                continue;
            }

            foreach (var element in FlattenJsonLd(document.RootElement))
            {
                if (!IsArticleJson(element))
                {
                    continue;
                }

                var title = JsonString(element, "headline") ?? JsonString(element, "name");
                var rawUrl = JsonString(element, "url") ?? JsonString(element, "mainEntityOfPage");
                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                var canonical = ResolveOptional(pageUri, rawUrl) ?? CanonicalUrl(html, pageUri) ?? pageUri.ToString();
                yield return CreateStory(
                    sourceId,
                    title,
                    JsonString(element, "description") ?? string.Empty,
                    canonical,
                    JsonString(element, "datePublished") ?? JsonString(element, "dateModified"),
                    ResolveOptional(pageUri, JsonImage(element)),
                    JsonString(element, "@id") ?? canonical,
                    canonical);
            }
        }
    }

    private static void AddHtmlCandidateStories(List<Story> stories, string containerHtml, string sourceId, Uri pageUri)
    {
        foreach (Match link in AnchorRegex().Matches(containerHtml))
        {
            var href = link.Groups[1].Value;
            var title = StripMarkup(link.Groups[2].Value);
            if (title.Length < 8 || !TryResolve(pageUri, href, out var uri))
            {
                continue;
            }

            var neighborhood = NearbyMarkup(containerHtml, link.Index);
            var summary = ParagraphRegex().Match(neighborhood).Groups[1].Value;
            var image = ImageRegex().Match(neighborhood).Groups[1].Value;
            var published = TimeRegex().Match(neighborhood).Groups[1].Value;
            stories.Add(CreateStory(
                sourceId,
                title,
                summary,
                uri.ToString(),
                published,
                ResolveOptional(pageUri, image),
                uri.ToString(),
                uri.ToString()));
        }
    }

    private static List<Story> DeduplicateStories(IEnumerable<Story> stories)
    {
        var byUrl = new Dictionary<string, Story>(StringComparer.OrdinalIgnoreCase);
        foreach (var story in stories)
        {
            var key = story.CanonicalUrl ?? story.Url;
            if (!byUrl.ContainsKey(key))
            {
                byUrl[key] = story;
            }
        }

        return byUrl.Values
            .Where(story => !LooksLikeNavigation(story.Title, story.Url))
            .OrderByDescending(story => story.PublishedAt)
            .ToList();
    }

    private static bool LooksLikeNavigation(string title, string url)
    {
        var text = title.Trim().ToLowerInvariant();
        if (text is "home" or "about" or "contact" or "privacy" or "terms" or "login")
        {
            return true;
        }

        return url.Contains("/tag/", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("/category/", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("/author/", StringComparison.OrdinalIgnoreCase);
    }

    private static Story CreateStory(
        string sourceId,
        string title,
        string summary,
        string url,
        string? published,
        string? imageUrl,
        string? externalId = null,
        string? canonicalUrl = null)
    {
        var now = DateTimeOffset.UtcNow;
        var normalizedUrl = NormalizeStoryUrl(url);
        var normalizedCanonical = NormalizeStoryUrl(canonicalUrl ?? normalizedUrl);
        return new Story
        {
            Id = StableStoryId(sourceId, externalId, normalizedCanonical, title),
            SourceId = sourceId,
            Title = Truncate(StripMarkup(title), 180),
            Summary = Truncate(StripMarkup(summary), 420),
            Url = normalizedUrl,
            ImageUrl = string.IsNullOrWhiteSpace(imageUrl) ? null : NormalizeStoryUrl(imageUrl),
            PublishedAt = ParseDate(published) ?? now,
            DiscoveredAt = now,
            ExternalId = externalId,
            CanonicalUrl = normalizedCanonical
        };
    }

    private static string? Value(XElement element, string localName) =>
        element.Elements().FirstOrDefault(child => child.Name.LocalName == localName)?.Value;

    private static Uri Resolve(Uri baseUri, string value) =>
        TryResolve(baseUri, value, out var uri) ? uri : baseUri;

    private static bool TryResolve(Uri baseUri, string value, out Uri uri)
    {
        uri = baseUri;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = WebUtility.HtmlDecode(value.Trim());
        if (trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            trimmed = $"{baseUri.Scheme}:{trimmed}";
        }

        if (!Uri.TryCreate(baseUri, trimmed, out var resolved) ||
            (resolved.Scheme != Uri.UriSchemeHttp && resolved.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        var builder = new UriBuilder(resolved) { Fragment = string.Empty };
        uri = builder.Uri;
        return true;
    }

    private static string? ResolveOptional(Uri baseUri, string? value) =>
        value is not null && TryResolve(baseUri, value, out var uri) ? uri.ToString() : null;

    private static string StableId(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..18];

    private static string Truncate(string value, int maxLength) =>
        value.Length > maxLength ? $"{value[..maxLength]}..." : value;

    private static string? ImageFromFeedItem(XElement item, string markup, Uri baseUri)
    {
        foreach (var element in item.Elements())
        {
            var local = element.Name.LocalName;
            var url = element.Attribute("url")?.Value;
            if (url is null)
            {
                continue;
            }

            if (local is "enclosure" &&
                (element.Attribute("type")?.Value.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true ||
                 element.Attribute("type") is null))
            {
                return ResolveOptional(baseUri, url);
            }

            if (local is "content" or "thumbnail")
            {
                return ResolveOptional(baseUri, url);
            }
        }

        var image = ImageRegex().Match(markup).Groups[1].Value;
        return ResolveOptional(baseUri, image);
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

    private static string? CanonicalUrl(string html, Uri pageUri)
    {
        foreach (Match match in LinkTagRegex().Matches(html))
        {
            var rel = Attribute(match.Value, "rel");
            var href = Attribute(match.Value, "href");
            if (href is not null &&
                rel?.Contains("canonical", StringComparison.OrdinalIgnoreCase) == true &&
                TryResolve(pageUri, href, out var canonical))
            {
                return canonical.ToString();
            }
        }

        return null;
    }

    private static string? Attribute(string tag, string name) =>
        Regex.Match(tag, $"""(?is)\b{Regex.Escape(name)}\s*=\s*["']([^"']+)["']""").Groups[1].Value switch
        {
            "" => null,
            var value => WebUtility.HtmlDecode(value)
        };

    private static JsonDocument? TryParseJson(string json)
    {
        try
        {
            return JsonDocument.Parse(WebUtility.HtmlDecode(json));
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<JsonElement> FlattenJsonLd(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                foreach (var child in FlattenJsonLd(item))
                {
                    yield return child;
                }
            }
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("@graph", out var graph))
            {
                foreach (var child in FlattenJsonLd(graph))
                {
                    yield return child;
                }
            }

            yield return root;
        }
    }

    private static bool IsArticleJson(JsonElement element)
    {
        var type = JsonString(element, "@type");
        return type is not null &&
               (type.Contains("Article", StringComparison.OrdinalIgnoreCase) ||
                type.Contains("NewsArticle", StringComparison.OrdinalIgnoreCase) ||
                type.Contains("BlogPosting", StringComparison.OrdinalIgnoreCase));
    }

    private static string? JsonString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Object when value.TryGetProperty("@id", out var id) => id.GetString(),
            JsonValueKind.Object when value.TryGetProperty("url", out var url) => url.GetString(),
            _ => null
        };
    }

    private static string? JsonImage(JsonElement element)
    {
        if (!element.TryGetProperty("image", out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Array => value.EnumerateArray().Select(JsonImageValue).FirstOrDefault(static image => image is not null),
            JsonValueKind.Object => JsonImageValue(value),
            _ => null
        };
    }

    private static string? JsonImageValue(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Object when value.TryGetProperty("url", out var url) => url.GetString(),
            _ => null
        };

    private static string NearbyMarkup(string html, int index)
    {
        var start = Math.Max(0, index - 500);
        var length = Math.Min(html.Length - start, 1400);
        return html.Substring(start, length);
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    [GeneratedRegex("""<link\b[^>]*>""", RegexOptions.IgnoreCase)]
    private static partial Regex LinkTagRegex();

    [GeneratedRegex("""<meta\b[^>]*>""", RegexOptions.IgnoreCase)]
    private static partial Regex MetaTagRegex();

    [GeneratedRegex("""<script\b[^>]*type=["']application/ld\+json["'][^>]*>([\s\S]*?)</script>""", RegexOptions.IgnoreCase)]
    private static partial Regex JsonLdRegex();

    [GeneratedRegex("""<article\b[^>]*>([\s\S]*?)</article>""", RegexOptions.IgnoreCase)]
    private static partial Regex ArticleRegex();

    [GeneratedRegex("""<main\b[^>]*>([\s\S]*?)</main>""", RegexOptions.IgnoreCase)]
    private static partial Regex MainRegex();

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

    [GeneratedRegex("""<(script|style)\b[^>]*>[\s\S]*?</\1>""", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptOrStyleRegex();

    [GeneratedRegex("""<[^>]+>""")]
    private static partial Regex MarkupRegex();

    [GeneratedRegex("""\s+([。．、，.!?])""")]
    private static partial Regex SpaceBeforePunctuationRegex();
}
