import AsyncStorage from '@react-native-async-storage/async-storage';

import {
  feedSource,
  findFeedCandidates,
  normalizeUrl,
  parseFeed,
  parseWebsite,
  sourceFallbackTitle,
  sourceIconFromHtml,
  sourceIdFor,
  sourceTitleFromHtml,
} from './feed';
import type {RefreshResult, Snapshot, Source, Story} from '../types';

const STORAGE_KEY = 'tide.snapshot.v1';
const MAX_STORIES = 500;
const ACCENTS = ['#d98368', '#698c7a', '#7d83b2', '#c59550', '#9d79a3', '#568da0'];
const EMPTY_SNAPSHOT: Snapshot = {sources: [], stories: []};

interface FetchedSource {
  source: Source;
  stories: Story[];
}

const download = async (url: string): Promise<string> => {
  const response = await fetch(url, {headers: {Accept: 'text/html, application/rss+xml, application/atom+xml, application/xml;q=0.9'}});
  if (!response.ok) {
    throw new Error(`${response.status} ${response.statusText}`);
  }
  return response.text();
};

const withAccent = (source: Source, accent: string): Source => ({...source, accent});

const fetchSource = async (rawUrl: string, accent: string): Promise<FetchedSource> => {
  const pageUrl = normalizeUrl(rawUrl);
  const sourceId = sourceIdFor(pageUrl);
  const body = await download(pageUrl);
  const directFeed = parseFeed(body, sourceId, pageUrl);
  if (directFeed) {
    return feedSource(sourceId, pageUrl, pageUrl, directFeed, accent);
  }

  const iconUrl = sourceIconFromHtml(body, pageUrl);
  for (const candidate of findFeedCandidates(body, pageUrl)) {
    try {
      const feed = parseFeed(await download(candidate), sourceId, candidate);
      if (feed) {
        return feedSource(sourceId, pageUrl, candidate, feed, accent, iconUrl);
      }
    } catch {
      // Candidate probing is best-effort. The HTML fallback still gives the site a useful card.
    }
  }

  return {
    source: {
      id: sourceId,
      title: sourceTitleFromHtml(body, sourceFallbackTitle(pageUrl)),
      url: pageUrl,
      sourceKind: 'website',
      iconUrl,
      accent,
      addedAt: new Date().toISOString(),
    },
    stories: parseWebsite(body, sourceId, pageUrl),
  };
};

const save = async (snapshot: Snapshot): Promise<Snapshot> => {
  await AsyncStorage.setItem(STORAGE_KEY, JSON.stringify(snapshot));
  return snapshot;
};

const mergedStories = (current: Story[], incoming: Story[]): Story[] => {
  const previous = new Map(current.map(story => [story.id, story]));
  for (const story of incoming) {
    const existing = previous.get(story.id);
    previous.set(story.id, existing ? {...story, isRead: existing.isRead, isSaved: existing.isSaved} : story);
  }
  return Array.from(previous.values())
    .sort((left, right) => right.publishedAt.localeCompare(left.publishedAt))
    .slice(0, MAX_STORIES);
};

export const loadSnapshot = async (): Promise<Snapshot> => {
  const serialized = await AsyncStorage.getItem(STORAGE_KEY);
  if (!serialized) {
    return EMPTY_SNAPSHOT;
  }
  try {
    return JSON.parse(serialized) as Snapshot;
  } catch {
    return EMPTY_SNAPSHOT;
  }
};

export const addSource = async (snapshot: Snapshot, url: string): Promise<Snapshot> => {
  const fetched = await fetchSource(url, ACCENTS[snapshot.sources.length % ACCENTS.length]);
  if (snapshot.sources.some(source => source.id === fetched.source.id)) {
    throw new Error('このサイトはすでに登録されています。');
  }
  return save({
    sources: [...snapshot.sources, fetched.source],
    stories: mergedStories(snapshot.stories, fetched.stories),
    lastRefreshedAt: new Date().toISOString(),
  });
};

export const refreshSources = async (snapshot: Snapshot): Promise<RefreshResult> => {
  const results = await Promise.allSettled(
    snapshot.sources.map(source => fetchSource(source.url, source.accent)),
  );
  const failedSources: string[] = [];
  let stories = snapshot.stories;
  const sources = [...snapshot.sources];

  results.forEach((result, index) => {
    if (result.status === 'rejected') {
      failedSources.push(snapshot.sources[index].title);
      return;
    }
    const previous = snapshot.sources[index];
    sources[index] = withAccent({...result.value.source, addedAt: previous.addedAt}, previous.accent);
    stories = mergedStories(stories, result.value.stories);
  });

  return {
    snapshot: await save({sources, stories, lastRefreshedAt: new Date().toISOString()}),
    failedSources,
  };
};

export const removeSource = async (snapshot: Snapshot, sourceId: string): Promise<Snapshot> =>
  save({
    ...snapshot,
    sources: snapshot.sources.filter(source => source.id !== sourceId),
    stories: snapshot.stories.filter(story => story.sourceId !== sourceId),
  });

export const markStoryRead = async (snapshot: Snapshot, storyId: string): Promise<Snapshot> =>
  save({
    ...snapshot,
    stories: snapshot.stories.map(story => (story.id === storyId ? {...story, isRead: true} : story)),
  });

export const markAllRead = async (snapshot: Snapshot): Promise<Snapshot> =>
  save({...snapshot, stories: snapshot.stories.map(story => ({...story, isRead: true}))});

export const toggleSaved = async (snapshot: Snapshot, storyId: string): Promise<Snapshot> =>
  save({
    ...snapshot,
    stories: snapshot.stories.map(story =>
      story.id === storyId ? {...story, isSaved: !story.isSaved} : story,
    ),
  });
