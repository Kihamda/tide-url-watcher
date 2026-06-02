export type InboxFilter = 'all' | 'unread' | 'saved';
export type SourceKind = 'feed' | 'website';

export interface Source {
  id: string;
  title: string;
  url: string;
  feedUrl?: string;
  sourceKind: SourceKind;
  iconUrl?: string;
  accent: string;
  addedAt: string;
}

export interface Story {
  id: string;
  sourceId: string;
  title: string;
  summary: string;
  url: string;
  imageUrl?: string;
  publishedAt: string;
  discoveredAt: string;
  isRead: boolean;
  isSaved: boolean;
}

export interface Snapshot {
  sources: Source[];
  stories: Story[];
  lastRefreshedAt?: string;
}

export interface RefreshResult {
  snapshot: Snapshot;
  failedSources: string[];
}
