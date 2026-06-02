import {XMLParser} from 'fast-xml-parser';

import type {Source, Story} from '../types';

const parser = new XMLParser({
  ignoreAttributes: false,
  attributeNamePrefix: '@_',
  trimValues: true,
});

type JsonObject = Record<string, unknown>;

const object = (value: unknown): JsonObject =>
  value !== null && typeof value === 'object' ? (value as JsonObject) : {};

const array = <T>(value: T | T[] | undefined): T[] => {
  if (value === undefined) {
    return [];
  }
  return Array.isArray(value) ? value : [value];
};

const text = (value: unknown): string => {
  if (typeof value === 'string' || typeof value === 'number') {
    return String(value);
  }
  if (value === null || value === undefined) {
    return '';
  }
  const candidate = object(value);
  const nested = candidate['#text'] ?? candidate.__cdata;
  return nested === undefined ? '' : text(nested);
};

const absoluteUrl = (value: string, baseUrl: string): string => {
  try {
    return new URL(value, baseUrl).toString();
  } catch {
    return value;
  }
};

const stableId = (value: string): string => {
  let hash = 5381;
  for (let index = 0; index < value.length; index += 1) {
    hash = (hash * 33 + value.charCodeAt(index)) % 2147483647;
  }
  return `story-${hash.toString(36)}`;
};

export const stripMarkup = (value: string): string =>
  value
    .replace(/<style\b[^>]*>[\s\S]*?<\/style>/gi, ' ')
    .replace(/<script\b[^>]*>[\s\S]*?<\/script>/gi, ' ')
    .replace(/<[^>]+>/g, ' ')
    .replace(/&nbsp;/gi, ' ')
    .replace(/&amp;/gi, '&')
    .replace(/&lt;/gi, '<')
    .replace(/&gt;/gi, '>')
    .replace(/&quot;/gi, '"')
    .replace(/&#39;/gi, "'")
    .replace(/\s+/g, ' ')
    .trim();

const imageFromMarkup = (value: string, baseUrl: string): string | undefined => {
  const match = value.match(/<img\b[^>]*\bsrc=["']([^"']+)["']/i);
  return match?.[1] ? absoluteUrl(match[1], baseUrl) : undefined;
};

const story = (
  sourceId: string,
  title: string,
  summary: string,
  url: string,
  publishedAt: string | undefined,
  imageUrl?: string,
): Story => {
  const discoveredAt = new Date().toISOString();
  return {
    id: stableId(`${sourceId}:${url}:${title}`),
    sourceId,
    title: stripMarkup(title).slice(0, 180),
    summary: stripMarkup(summary).slice(0, 420),
    url,
    imageUrl,
    publishedAt: publishedAt || discoveredAt,
    discoveredAt,
    isRead: false,
    isSaved: false,
  };
};

const rssStories = (channel: JsonObject, sourceId: string, feedUrl: string): Story[] =>
  array(channel.item).flatMap(rawItem => {
    const item = object(rawItem);
    const title = text(item.title);
    const url = text(item.link) || text(item.guid);
    if (!title || !url) {
      return [];
    }
    const summary = text(item.description ?? item['content:encoded']);
    const enclosure = object(item.enclosure);
    const media = object(item['media:content']);
    const imageUrl = text(enclosure['@_url'] ?? media['@_url']) || imageFromMarkup(summary, feedUrl);
    return [
      story(
        sourceId,
        title,
        summary,
        absoluteUrl(url, feedUrl),
        text(item.pubDate ?? item.date),
        imageUrl ? absoluteUrl(imageUrl, feedUrl) : undefined,
      ),
    ];
  });

const atomLink = (value: unknown): string => {
  const links = array(value);
  const alternate = links.find(link => object(link)['@_rel'] === 'alternate') ?? links[0];
  return text(object(alternate)['@_href'] ?? alternate);
};

const atomStories = (feed: JsonObject, sourceId: string, feedUrl: string): Story[] =>
  array(feed.entry).flatMap(rawEntry => {
    const entry = object(rawEntry);
    const title = text(entry.title);
    const url = atomLink(entry.link) || text(entry.id);
    if (!title || !url) {
      return [];
    }
    const summary = text(entry.summary ?? entry.content);
    return [
      story(
        sourceId,
        title,
        summary,
        absoluteUrl(url, feedUrl),
        text(entry.published ?? entry.updated),
        imageFromMarkup(summary, feedUrl),
      ),
    ];
  });

export interface ParsedFeed {
  title?: string;
  stories: Story[];
}

export const parseFeed = (xml: string, sourceId: string, feedUrl: string): ParsedFeed | null => {
  try {
    const parsed = object(parser.parse(xml));
    const rss = object(parsed.rss);
    const channel = object(rss.channel);
    if (Object.keys(channel).length) {
      return {
        title: text(channel.title),
        stories: rssStories(channel, sourceId, feedUrl).slice(0, 80),
      };
    }

    const atom = object(parsed.feed);
    if (Object.keys(atom).length) {
      return {
        title: text(atom.title),
        stories: atomStories(atom, sourceId, feedUrl).slice(0, 80),
      };
    }
  } catch {
    return null;
  }
  return null;
};

const attribute = (tag: string, name: string): string | undefined => {
  const match = tag.match(new RegExp(`\\b${name}=["']([^"']+)["']`, 'i'));
  return match?.[1];
};

export const findFeedCandidates = (html: string, pageUrl: string): string[] => {
  const candidates = Array.from(html.matchAll(/<link\b[^>]*>/gi))
    .flatMap(match => {
      const tag = match[0];
      const rel = attribute(tag, 'rel') ?? '';
      const type = attribute(tag, 'type') ?? '';
      const href = attribute(tag, 'href');
      return /alternate/i.test(rel) && /(rss|atom|xml)/i.test(type) && href
        ? [absoluteUrl(href, pageUrl)]
        : [];
    });

  for (const path of ['/feed', '/feed.xml', '/rss.xml', '/atom.xml']) {
    candidates.push(absoluteUrl(path, pageUrl));
  }
  return Array.from(new Set(candidates));
};

const metaContent = (html: string, name: string): string | undefined => {
  const tags = html.match(/<meta\b[^>]*>/gi) ?? [];
  for (const tag of tags) {
    const key = attribute(tag, 'name') ?? attribute(tag, 'property');
    if (key?.toLowerCase() === name.toLowerCase()) {
      return attribute(tag, 'content');
    }
  }
  return undefined;
};

export const parseWebsite = (html: string, sourceId: string, pageUrl: string): Story[] => {
  const stories = Array.from(html.matchAll(/<article\b[^>]*>([\s\S]*?)<\/article>/gi))
    .flatMap(match => {
      const article = match[1];
      const link = article.match(/<a\b[^>]*\bhref=["']([^"']+)["'][^>]*>([\s\S]*?)<\/a>/i);
      const title = stripMarkup(link?.[2] ?? '');
      if (!link?.[1] || title.length < 8) {
        return [];
      }
      const summary = article.match(/<p\b[^>]*>([\s\S]*?)<\/p>/i)?.[1] ?? '';
      const imageUrl = article.match(/<img\b[^>]*\b(?:src|data-src)=["']([^"']+)["']/i)?.[1];
      const publishedAt = article.match(/<time\b[^>]*\bdatetime=["']([^"']+)["']/i)?.[1];
      return [
        story(
          sourceId,
          title,
          summary,
          absoluteUrl(link[1], pageUrl),
          publishedAt,
          imageUrl ? absoluteUrl(imageUrl, pageUrl) : undefined,
        ),
      ];
    });

  if (stories.length) {
    return stories.slice(0, 40);
  }

  const title = stripMarkup(html.match(/<title\b[^>]*>([\s\S]*?)<\/title>/i)?.[1] ?? pageUrl);
  return [
    story(
      sourceId,
      title,
      metaContent(html, 'description') ?? metaContent(html, 'og:description') ?? '',
      pageUrl,
      undefined,
      metaContent(html, 'og:image'),
    ),
  ];
};

export const sourceTitleFromHtml = (html: string, fallback: string): string =>
  metaContent(html, 'og:site_name') ||
  stripMarkup(html.match(/<title\b[^>]*>([\s\S]*?)<\/title>/i)?.[1] ?? '') ||
  fallback;

export const sourceIconFromHtml = (html: string, pageUrl: string): string | undefined => {
  const iconTag = (html.match(/<link\b[^>]*>/gi) ?? []).find(tag =>
    /icon/i.test(attribute(tag, 'rel') ?? ''),
  );
  const href = iconTag ? attribute(iconTag, 'href') : undefined;
  return href ? absoluteUrl(href, pageUrl) : absoluteUrl('/favicon.ico', pageUrl);
};

export const normalizeUrl = (rawUrl: string): string => {
  const withProtocol = rawUrl.trim().match(/^https?:\/\//i) ? rawUrl.trim() : `https://${rawUrl.trim()}`;
  if (!/^https?:\/\//i.test(withProtocol)) {
    throw new Error('http または https のURLを入力してください。');
  }
  return new URL(withProtocol).toString().split('#')[0];
};

export const sourceIdFor = (url: string): string => stableId(`source:${url}`).replace('story-', 'source-');

export const sourceFallbackTitle = (url: string): string => {
  return url.match(/^https?:\/\/([^/?#]+)/i)?.[1]?.replace(/^www\./, '') ?? url;
};

export const feedSource = (
  sourceId: string,
  pageUrl: string,
  feedUrl: string,
  feed: ParsedFeed,
  accent: string,
  iconUrl?: string,
): {source: Source; stories: Story[]} => ({
  source: {
    id: sourceId,
    title: feed.title || sourceFallbackTitle(pageUrl),
    url: pageUrl,
    feedUrl,
    sourceKind: 'feed',
    iconUrl,
    accent,
    addedAt: new Date().toISOString(),
  },
  stories: feed.stories,
});
