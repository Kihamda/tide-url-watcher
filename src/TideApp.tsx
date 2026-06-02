import React, {useEffect, useMemo, useState} from 'react';
import {
  ActivityIndicator,
  Alert,
  Image,
  Linking,
  Modal,
  Pressable,
  ScrollView,
  StatusBar,
  StyleSheet,
  Text,
  TextInput,
  useWindowDimensions,
  View,
} from 'react-native';

import {
  addSource,
  loadSnapshot,
  markAllRead,
  markStoryRead,
  refreshSources,
  removeSource,
  toggleSaved,
} from './core/repository';
import type {InboxFilter, Snapshot, Source, Story} from './types';

const EMPTY: Snapshot = {sources: [], stories: []};

const relativeTime = (value?: string): string => {
  if (!value) return 'まだ更新されていません';
  const minutes = Math.max(0, Math.floor((Date.now() - new Date(value).getTime()) / 60000));
  if (minutes < 1) return 'たった今';
  if (minutes < 60) return `${minutes}分前`;
  if (minutes < 1440) return `${Math.floor(minutes / 60)}時間前`;
  return `${Math.floor(minutes / 1440)}日前`;
};

const host = (url: string): string => {
  return url.match(/^https?:\/\/([^/?#]+)/i)?.[1]?.replace(/^www\./, '') ?? url;
};

const Glyph = ({children}: {children: string}) => <Text style={styles.glyph}>{children}</Text>;

const SourceMark = ({source, compact = false}: {source: Source; compact?: boolean}) => (
  <View style={[styles.sourceMark, compact && styles.sourceMarkCompact, {backgroundColor: source.accent}]}>
    {source.iconUrl ? (
      <Image source={{uri: source.iconUrl}} style={styles.sourceMarkImage} />
    ) : (
      <Text style={[styles.sourceMarkText, compact && styles.sourceMarkTextCompact]}>
        {source.title.slice(0, 1).toUpperCase()}
      </Text>
    )}
  </View>
);

const NavRow = ({
  glyph,
  title,
  count,
  selected,
  onPress,
}: {
  glyph: string;
  title: string;
  count: number;
  selected: boolean;
  onPress: () => void;
}) => (
  <Pressable style={[styles.navRow, selected && styles.navRowSelected]} onPress={onPress}>
    <Glyph>{glyph}</Glyph>
    <Text style={[styles.navLabel, selected && styles.navLabelSelected]}>{title}</Text>
    <Text style={styles.navCount}>{count}</Text>
  </Pressable>
);

const StoryCard = ({
  story,
  source,
  featured,
  onOpen,
  onSave,
}: {
  story: Story;
  source: Source;
  featured: boolean;
  onOpen: () => void;
  onSave: () => void;
}) => (
  <View style={[styles.storyCard, featured && styles.storyCardFeatured, story.isRead && styles.storyCardRead]}>
    <Pressable
      accessibilityLabel={story.title}
      onPress={onOpen}
      style={[styles.storyImageWrap, featured && styles.storyImageFeatured, !story.imageUrl && {backgroundColor: source.accent}]}>
      {story.imageUrl ? (
        <Image source={{uri: story.imageUrl}} resizeMode="cover" style={styles.storyImage} />
      ) : (
        <Text style={styles.storyFallback}>{source.title.slice(0, 1).toUpperCase()}</Text>
      )}
    </Pressable>
    <View style={[styles.storyBody, featured && styles.storyBodyFeatured]}>
      <View style={styles.storyMeta}>
        <SourceMark source={source} compact />
        <Text style={styles.storySource}>{source.title}</Text>
        <Text style={styles.storyMetaDot}>·</Text>
        <Text style={styles.storyTime}>{relativeTime(story.publishedAt)}</Text>
        {!story.isRead ? <Text style={styles.newLabel}>NEW</Text> : null}
      </View>
      <Pressable onPress={onOpen}>
        <Text numberOfLines={3} style={[styles.storyTitle, featured && styles.storyTitleFeatured]}>
          {story.title}
        </Text>
      </Pressable>
      {story.summary ? (
        <Text numberOfLines={3} style={styles.storySummary}>
          {story.summary}
        </Text>
      ) : null}
      <View style={styles.storyFooter}>
        <Text numberOfLines={1} style={styles.storyHost}>{host(story.url)}</Text>
        <View style={styles.storyButtons}>
          <Pressable hitSlop={8} onPress={onSave} style={styles.iconPressable}>
            <Text style={[styles.iconText, story.isSaved && styles.iconTextActive]}>{story.isSaved ? '◆' : '◇'}</Text>
          </Pressable>
          <Pressable hitSlop={8} onPress={onOpen} style={styles.iconPressable}>
            <Text style={styles.iconText}>↗</Text>
          </Pressable>
        </View>
      </View>
    </View>
  </View>
);

const AddSourceDialog = ({
  visible,
  busy,
  error,
  onDismiss,
  onSubmit,
}: {
  visible: boolean;
  busy: boolean;
  error?: string;
  onDismiss: () => void;
  onSubmit: (url: string) => void;
}) => {
  const [url, setUrl] = useState('');
  return (
    <Modal animationType="fade" transparent visible={visible} onRequestClose={onDismiss}>
      <Pressable style={styles.modalBackdrop} onPress={onDismiss}>
        <Pressable style={styles.modalCard} onPress={() => undefined}>
          <Pressable hitSlop={9} onPress={onDismiss} style={styles.modalClose}>
            <Text style={styles.iconText}>×</Text>
          </Pressable>
          <View style={styles.modalSymbol}><Text style={styles.modalSymbolText}>＋</Text></View>
          <Text style={styles.eyebrow}>NEW SOURCE</Text>
          <Text style={styles.modalTitle}>気になるサイトを追加</Text>
          <Text style={styles.modalCopy}>
            トップページやRSS URLを入力してください。フィードがあれば自動で見つけます。
          </Text>
          <View style={styles.urlField}>
            <Text style={styles.urlGlyph}>◎</Text>
            <TextInput
              autoCapitalize="none"
              autoCorrect={false}
              autoFocus
              editable={!busy}
              onChangeText={setUrl}
              onSubmitEditing={() => url.trim() && onSubmit(url)}
              placeholder="example.com/journal"
              placeholderTextColor="#b3bab6"
              style={styles.urlInput}
              value={url}
            />
          </View>
          {error ? <Text style={styles.formError}>{error}</Text> : null}
          <Pressable
            disabled={busy || !url.trim()}
            onPress={() => onSubmit(url)}
            style={({pressed}) => [styles.primaryButton, styles.modalSubmit, pressed && styles.buttonPressed, (busy || !url.trim()) && styles.buttonDisabled]}>
            {busy ? <ActivityIndicator color="#fff" size="small" /> : <Text style={styles.primaryButtonGlyph}>✦</Text>}
            <Text style={styles.primaryButtonText}>{busy ? 'サイトを解析中...' : 'ウォッチを始める'}</Text>
          </Pressable>
          <Text style={styles.modalFootnote}>登録したデータは、この端末の中にだけ保存されます。</Text>
        </Pressable>
      </Pressable>
    </Modal>
  );
};

export default function TideApp() {
  const {width} = useWindowDimensions();
  const [snapshot, setSnapshot] = useState<Snapshot>(EMPTY);
  const [filter, setFilter] = useState<InboxFilter>('all');
  const [activeSource, setActiveSource] = useState('all');
  const [query, setQuery] = useState('');
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [adding, setAdding] = useState(false);
  const [dialogVisible, setDialogVisible] = useState(false);
  const [dialogError, setDialogError] = useState<string>();
  const showSidebar = width >= 780;
  const showInsight = width >= 1120;

  useEffect(() => {
    loadSnapshot()
      .then(setSnapshot)
      .catch(error => Alert.alert('読み込みに失敗しました', String(error)))
      .finally(() => setLoading(false));
  }, []);

  const sources = useMemo(() => new Map(snapshot.sources.map(source => [source.id, source])), [snapshot.sources]);
  const unreadCount = snapshot.stories.filter(story => !story.isRead).length;
  const savedCount = snapshot.stories.filter(story => story.isSaved).length;
  const visibleStories = snapshot.stories.filter(story => {
    if (activeSource !== 'all' && story.sourceId !== activeSource) return false;
    if (filter === 'unread' && story.isRead) return false;
    if (filter === 'saved' && !story.isSaved) return false;
    const haystack = `${story.title} ${story.summary} ${sources.get(story.sourceId)?.title ?? ''}`.toLowerCase();
    return haystack.includes(query.trim().toLowerCase());
  });
  const currentSource = activeSource === 'all' ? undefined : sources.get(activeSource);

  const submitSource = async (url: string) => {
    setAdding(true);
    setDialogError(undefined);
    try {
      setSnapshot(await addSource(snapshot, url));
      setDialogVisible(false);
    } catch (error) {
      setDialogError(String(error).replace(/^Error:\s*/, ''));
    } finally {
      setAdding(false);
    }
  };

  const refresh = async () => {
    setRefreshing(true);
    try {
      const result = await refreshSources(snapshot);
      setSnapshot(result.snapshot);
      if (result.failedSources.length) {
        Alert.alert('一部のサイトを更新できませんでした', result.failedSources.join('\n'));
      }
    } catch (error) {
      Alert.alert('更新に失敗しました', String(error));
    } finally {
      setRefreshing(false);
    }
  };

  const openStory = async (story: Story) => {
    if (!story.isRead) setSnapshot(await markStoryRead(snapshot, story.id));
    await Linking.openURL(story.url);
  };

  const confirmRemove = (source: Source) => {
    Alert.alert(`${source.title} を削除しますか？`, 'このサイトから届いた記事も一覧から外れます。', [
      {text: 'キャンセル', style: 'cancel'},
      {
        text: '削除',
        style: 'destructive',
        onPress: () => {
          removeSource(snapshot, source.id).then(setSnapshot);
          if (activeSource === source.id) setActiveSource('all');
        },
      },
    ]);
  };

  return (
    <View style={styles.app}>
      <StatusBar barStyle="dark-content" backgroundColor="#f0f1eb" />
      <View style={styles.windowBar}>
        <View style={styles.brand}><Text style={styles.brandSigil}>T</Text><Text style={styles.brandName}>TIDE</Text></View>
        <Text style={styles.windowContext}>◷　最終確認 {relativeTime(snapshot.lastRefreshedAt)}</Text>
        <Text style={styles.windowMenu}>☰</Text>
      </View>
      <View style={styles.desktop}>
        {showSidebar ? (
          <View style={styles.sidebar}>
            <Text style={styles.sidebarHeading}>LIBRARY</Text>
            <NavRow glyph="◉" title="すべての新着" count={snapshot.stories.length} selected={filter === 'all'} onPress={() => setFilter('all')} />
            <NavRow glyph="✦" title="未読" count={unreadCount} selected={filter === 'unread'} onPress={() => setFilter('unread')} />
            <NavRow glyph="◇" title="保存済み" count={savedCount} selected={filter === 'saved'} onPress={() => setFilter('saved')} />
            <View style={styles.sourceHeadingRow}>
              <Text style={styles.sidebarHeading}>SOURCES</Text>
              <Pressable hitSlop={8} onPress={() => setDialogVisible(true)}><Text style={styles.sidebarAdd}>＋</Text></Pressable>
            </View>
            <Pressable style={[styles.sourceRow, activeSource === 'all' && styles.sourceRowSelected]} onPress={() => setActiveSource('all')}>
              <View style={styles.allSourceMark}><Text style={styles.allSourceMarkText}>▦</Text></View>
              <Text style={styles.sourceRowLabel}>すべてのサイト</Text>
            </Pressable>
            {snapshot.sources.map(source => (
              <View key={source.id} style={styles.sourceRowWrap}>
                <Pressable style={[styles.sourceRow, activeSource === source.id && styles.sourceRowSelected]} onPress={() => setActiveSource(source.id)}>
                  <SourceMark compact source={source} />
                  <Text numberOfLines={1} style={styles.sourceRowLabel}>{source.title}</Text>
                  <Text style={styles.navCount}>{snapshot.stories.filter(story => story.sourceId === source.id && !story.isRead).length || ''}</Text>
                </Pressable>
                <Pressable hitSlop={7} onPress={() => confirmRemove(source)} style={styles.sourceRemove}><Text style={styles.sourceRemoveText}>×</Text></Pressable>
              </View>
            ))}
            <Text style={styles.settings}>⚙　環境設定</Text>
          </View>
        ) : null}

        <ScrollView contentContainerStyle={styles.mainContent} style={styles.main}>
          <View style={styles.contentHeader}>
            <View>
              <Text style={styles.eyebrow}>{currentSource ? currentSource.sourceKind.toUpperCase() : 'PERSONAL SIGNALS'}</Text>
              <Text style={styles.pageTitle}>{currentSource?.title ?? '新着の流れ'}</Text>
              <Text style={styles.pageDate}>{new Date().toLocaleDateString('ja-JP', {month: 'long', day: 'numeric', weekday: 'long'})}</Text>
            </View>
            <View style={styles.headerButtons}>
              <View style={styles.searchField}><Text style={styles.searchGlyph}>⌕</Text><TextInput onChangeText={setQuery} placeholder="記事を検索" placeholderTextColor="#aeb6b1" style={styles.searchInput} value={query} /></View>
              <Pressable disabled={refreshing} onPress={refresh} style={({pressed}) => [styles.secondaryButton, pressed && styles.buttonPressed]}><Text style={styles.secondaryButtonText}>{refreshing ? '◌' : '↻'}　更新</Text></Pressable>
              <Pressable onPress={() => setDialogVisible(true)} style={({pressed}) => [styles.primaryButton, pressed && styles.buttonPressed]}><Text style={styles.primaryButtonGlyph}>＋</Text><Text style={styles.primaryButtonText}>サイトを追加</Text></Pressable>
            </View>
          </View>
          <View style={styles.toolbar}>
            <Text style={styles.toolbarCount}>{visibleStories.length} STORIES　<Text style={styles.toolbarUnread}>{unreadCount ? `${unreadCount} UNREAD` : 'ALL CAUGHT UP'}</Text></Text>
            <Pressable onPress={() => markAllRead(snapshot).then(setSnapshot)}><Text style={styles.markRead}>✓✓　すべて既読にする</Text></Pressable>
          </View>

          {loading ? (
            <View style={styles.emptyState}><ActivityIndicator color="#60786b" /><Text style={styles.emptyCopy}>読み込み中...</Text></View>
          ) : snapshot.sources.length === 0 ? (
            <View style={styles.emptyState}>
              <View style={styles.emptyOrbit}><Text style={styles.emptyOrbitText}>◎</Text><View style={styles.emptyDot} /></View>
              <Text style={styles.eyebrow}>A QUIETER INBOX</Text>
              <Text style={styles.emptyTitle}>Webの新着を、{'\n'}心地よいひとつの場所へ。</Text>
              <Text style={styles.emptyCopy}>サイトを登録すると、RSSやページ内の記事を見つけて読みやすく整えます。</Text>
              <Pressable onPress={() => setDialogVisible(true)} style={styles.primaryButton}><Text style={styles.primaryButtonGlyph}>＋</Text><Text style={styles.primaryButtonText}>最初のサイトを追加</Text></Pressable>
            </View>
          ) : visibleStories.length === 0 ? (
            <View style={styles.emptyState}><Text style={styles.noResultsGlyph}>✓</Text><Text style={styles.emptyTitle}>ここは静かです。</Text><Text style={styles.emptyCopy}>この条件に合う新着はありません。</Text></View>
          ) : (
            <View style={styles.storyGrid}>
              {visibleStories.map((story, index) => {
                const source = sources.get(story.sourceId);
                if (!source) return null;
                return (
                  <View key={story.id} style={[styles.storyColumn, index === 0 && !query && activeSource === 'all' && filter !== 'saved' && styles.storyColumnFeatured]}>
                    <StoryCard
                      featured={index === 0 && !query && activeSource === 'all' && filter !== 'saved'}
                      onOpen={() => openStory(story)}
                      onSave={() => toggleSaved(snapshot, story.id).then(setSnapshot)}
                      source={source}
                      story={story}
                    />
                  </View>
                );
              })}
            </View>
          )}
        </ScrollView>

        {showInsight ? (
          <View style={styles.insight}>
            <Text style={styles.eyebrow}>TODAY</Text>
            <Text style={styles.insightNumber}>{String(unreadCount).padStart(2, '0')}</Text>
            <Text style={styles.insightCopy}>まだ読んでいない{'\n'}小さな発見があります。</Text>
            <View style={styles.insightDivider} />
            <View style={styles.insightStat}><Text style={styles.insightStatLabel}>ウォッチ中</Text><Text style={styles.insightStatValue}>{snapshot.sources.length}</Text></View>
            <View style={styles.insightStat}><Text style={styles.insightStatLabel}>保存した記事</Text><Text style={styles.insightStatValue}>{savedCount}</Text></View>
            <View style={styles.insightNote}><Text style={styles.insightNoteGlyph}>✦</Text><Text style={styles.insightNoteText}>RSSが見つからないサイトも、ページから記事らしい更新を探します。</Text></View>
            <Pressable disabled={refreshing} onPress={refresh} style={styles.insightRefresh}><Text style={styles.insightRefreshText}>↻　今すぐチェック</Text></Pressable>
          </View>
        ) : null}
      </View>
      <AddSourceDialog busy={adding} error={dialogError} onDismiss={() => setDialogVisible(false)} onSubmit={submitSource} visible={dialogVisible} />
    </View>
  );
}

const styles = StyleSheet.create({
  app: {flex: 1, backgroundColor: '#f0f1eb'},
  windowBar: {height: 44, flexDirection: 'row', alignItems: 'center', paddingHorizontal: 18, borderBottomWidth: 1, borderBottomColor: '#e3e5df', backgroundColor: '#f0f1eb'},
  brand: {width: 218, flexDirection: 'row', alignItems: 'center', gap: 9},
  brandSigil: {width: 21, height: 21, borderRadius: 11, color: '#f7f6ef', backgroundColor: '#53695d', fontFamily: 'Georgia', fontSize: 14, lineHeight: 21, textAlign: 'center'},
  brandName: {color: '#3d4842', fontSize: 10, fontWeight: '700', letterSpacing: 2.4},
  windowContext: {flex: 1, color: '#7d867f', fontSize: 11},
  windowMenu: {color: '#8d9690', fontSize: 17},
  desktop: {flex: 1, flexDirection: 'row'},
  sidebar: {width: 236, paddingHorizontal: 14, paddingTop: 25, paddingBottom: 18, borderRightWidth: 1, borderRightColor: '#e3e5df', backgroundColor: '#efefe9'},
  sidebarHeading: {marginLeft: 9, marginBottom: 9, color: '#9da39e', fontSize: 9, fontWeight: '700', letterSpacing: 1.7},
  navRow: {height: 38, flexDirection: 'row', alignItems: 'center', gap: 10, paddingHorizontal: 9, borderRadius: 8},
  navRowSelected: {backgroundColor: '#f8f8f4'},
  glyph: {width: 17, color: '#89958f', fontSize: 15},
  navLabel: {flex: 1, color: '#768078', fontSize: 12},
  navLabelSelected: {color: '#405249', fontWeight: '600'},
  navCount: {color: '#8e978f', fontSize: 10, fontWeight: '600'},
  sourceHeadingRow: {flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', marginTop: 28},
  sidebarAdd: {marginRight: 7, marginBottom: 7, color: '#718079', fontSize: 17},
  sourceRowWrap: {position: 'relative'},
  sourceRow: {height: 34, flexDirection: 'row', alignItems: 'center', gap: 9, paddingLeft: 8, paddingRight: 28, borderRadius: 7},
  sourceRowSelected: {backgroundColor: '#f6f7f2'},
  sourceRowLabel: {flex: 1, color: '#758078', fontSize: 11},
  sourceRemove: {position: 'absolute', right: 5, top: 7},
  sourceRemoveText: {color: '#aa8178', fontSize: 16},
  allSourceMark: {width: 21, height: 21, alignItems: 'center', justifyContent: 'center', borderRadius: 6, backgroundColor: '#dfe3dd'},
  allSourceMarkText: {color: '#728179', fontSize: 12},
  sourceMark: {width: 29, height: 29, alignItems: 'center', justifyContent: 'center', overflow: 'hidden', borderRadius: 9},
  sourceMarkCompact: {width: 21, height: 21, borderRadius: 6},
  sourceMarkImage: {width: '100%', height: '100%', backgroundColor: '#fff'},
  sourceMarkText: {color: '#fff', fontSize: 11, fontWeight: '700'},
  sourceMarkTextCompact: {fontSize: 8},
  settings: {marginTop: 'auto', paddingLeft: 9, color: '#808b85', fontSize: 12},
  main: {flex: 1, backgroundColor: '#faf9f5'},
  mainContent: {minHeight: '100%', paddingHorizontal: 38, paddingTop: 38, paddingBottom: 56},
  contentHeader: {flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', gap: 20},
  eyebrow: {color: '#9da39e', fontSize: 9, fontWeight: '700', letterSpacing: 1.8},
  pageTitle: {marginTop: 7, color: '#33433b', fontFamily: 'Georgia', fontSize: 37, letterSpacing: -1},
  pageDate: {marginTop: 4, color: '#9da39e', fontSize: 12},
  headerButtons: {flexDirection: 'row', alignItems: 'center', gap: 9},
  searchField: {width: 145, height: 38, flexDirection: 'row', alignItems: 'center', gap: 7, paddingHorizontal: 10, borderWidth: 1, borderColor: '#e5e8e3', borderRadius: 9, backgroundColor: '#fff'},
  searchGlyph: {color: '#9ca69f', fontSize: 20},
  searchInput: {flex: 1, padding: 0, color: '#4d5a54', fontSize: 12},
  primaryButton: {height: 38, flexDirection: 'row', alignItems: 'center', justifyContent: 'center', gap: 6, paddingHorizontal: 14, borderRadius: 9, backgroundColor: '#53695d'},
  primaryButtonGlyph: {color: '#fff', fontSize: 16, fontWeight: '700'},
  primaryButtonText: {color: '#fff', fontSize: 12, fontWeight: '700'},
  secondaryButton: {height: 38, justifyContent: 'center', paddingHorizontal: 12, borderRadius: 9, backgroundColor: '#eeefe9'},
  secondaryButtonText: {color: '#65746d', fontSize: 12, fontWeight: '700'},
  buttonPressed: {opacity: 0.76},
  buttonDisabled: {opacity: 0.55},
  toolbar: {height: 62, flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between'},
  toolbarCount: {color: '#9ca49f', fontSize: 10, fontWeight: '700', letterSpacing: 1.2},
  toolbarUnread: {color: '#ce846e'},
  markRead: {color: '#849089', fontSize: 11, fontWeight: '600'},
  storyGrid: {flexDirection: 'row', flexWrap: 'wrap', gap: 13},
  storyColumn: {width: '48.8%'},
  storyColumnFeatured: {width: '100%'},
  storyCard: {minHeight: 294, overflow: 'hidden', borderWidth: 1, borderColor: '#edf0eb', borderRadius: 12, backgroundColor: '#fff'},
  storyCardFeatured: {minHeight: 222, flexDirection: 'row'},
  storyCardRead: {opacity: 0.73},
  storyImageWrap: {height: 136, alignItems: 'center', justifyContent: 'center', overflow: 'hidden', backgroundColor: '#e4e7e1'},
  storyImageFeatured: {width: '43%', height: '100%'},
  storyImage: {width: '100%', height: '100%'},
  storyFallback: {color: 'rgba(255,255,255,0.62)', fontFamily: 'Georgia', fontSize: 58},
  storyBody: {flex: 1, paddingHorizontal: 17, paddingTop: 16, paddingBottom: 13},
  storyBodyFeatured: {justifyContent: 'center', paddingHorizontal: 25, paddingVertical: 18},
  storyMeta: {flexDirection: 'row', alignItems: 'center', gap: 7},
  storySource: {color: '#75827b', fontSize: 10, fontWeight: '700'},
  storyMetaDot: {color: '#c8ccc8', fontSize: 11},
  storyTime: {color: '#9ba39e', fontSize: 10},
  newLabel: {marginLeft: 'auto', color: '#c97e67', fontSize: 8, fontWeight: '700', letterSpacing: 1.2},
  storyTitle: {marginTop: 12, marginBottom: 7, color: '#3e4944', fontFamily: 'Georgia', fontSize: 20, lineHeight: 24},
  storyTitleFeatured: {fontSize: 29, lineHeight: 32},
  storySummary: {marginBottom: 15, color: '#88918c', fontSize: 11, lineHeight: 18},
  storyFooter: {flex: 1, flexDirection: 'row', alignItems: 'flex-end', justifyContent: 'space-between'},
  storyHost: {maxWidth: '70%', color: '#b0b5b2', fontSize: 10},
  storyButtons: {flexDirection: 'row', gap: 7},
  iconPressable: {width: 21, alignItems: 'center'},
  iconText: {color: '#85928c', fontSize: 18},
  iconTextActive: {color: '#617b6e'},
  insight: {width: 208, paddingHorizontal: 20, paddingTop: 35, borderLeftWidth: 1, borderLeftColor: '#e3e5df', backgroundColor: '#f2f2ed'},
  insightNumber: {marginTop: 26, color: '#52665b', fontFamily: 'Georgia', fontSize: 69, lineHeight: 65, letterSpacing: -5},
  insightCopy: {marginTop: 7, color: '#7d8882', fontSize: 12, lineHeight: 20},
  insightDivider: {height: 1, marginVertical: 20, backgroundColor: '#e0e3de'},
  insightStat: {height: 29, flexDirection: 'row', justifyContent: 'space-between'},
  insightStatLabel: {color: '#929a95', fontSize: 11},
  insightStatValue: {color: '#617068', fontSize: 12, fontWeight: '700'},
  insightNote: {marginTop: 24, padding: 12, borderRadius: 10, backgroundColor: '#e8ece6'},
  insightNoteGlyph: {color: '#d08b73', fontSize: 15},
  insightNoteText: {marginTop: 6, color: '#829088', fontSize: 10, lineHeight: 16},
  insightRefresh: {height: 37, justifyContent: 'center', marginTop: 14},
  insightRefreshText: {color: '#77857e', fontSize: 11, fontWeight: '700'},
  emptyState: {minHeight: 450, alignItems: 'center', justifyContent: 'center'},
  emptyOrbit: {width: 82, height: 82, alignItems: 'center', justifyContent: 'center', marginBottom: 20, borderWidth: 1, borderColor: '#d6ded8', borderRadius: 41},
  emptyOrbitText: {color: '#80988b', fontSize: 37},
  emptyDot: {position: 'absolute', top: 10, right: 3, width: 10, height: 10, borderRadius: 5, backgroundColor: '#e39a7f'},
  emptyTitle: {marginTop: 12, marginBottom: 8, color: '#425149', fontFamily: 'Georgia', fontSize: 30, lineHeight: 35, textAlign: 'center'},
  emptyCopy: {maxWidth: 370, marginTop: 8, marginBottom: 19, color: '#929c96', fontSize: 12, lineHeight: 21, textAlign: 'center'},
  noResultsGlyph: {color: '#80988b', fontSize: 30},
  modalBackdrop: {flex: 1, alignItems: 'center', justifyContent: 'center', padding: 20, backgroundColor: 'rgba(52,62,57,0.32)'},
  modalCard: {width: 430, maxWidth: '100%', paddingHorizontal: 30, paddingTop: 30, paddingBottom: 24, borderRadius: 17, backgroundColor: '#fbfaf6'},
  modalClose: {position: 'absolute', top: 12, right: 16},
  modalSymbol: {width: 42, height: 42, alignItems: 'center', justifyContent: 'center', marginBottom: 17, borderRadius: 21, backgroundColor: '#e8eee9'},
  modalSymbolText: {color: '#5b7568', fontSize: 21},
  modalTitle: {marginTop: 8, marginBottom: 8, color: '#394840', fontFamily: 'Georgia', fontSize: 27},
  modalCopy: {marginBottom: 20, color: '#8c9590', fontSize: 12, lineHeight: 20},
  urlField: {height: 45, flexDirection: 'row', alignItems: 'center', gap: 8, paddingHorizontal: 12, borderWidth: 1, borderColor: '#e2e6e1', borderRadius: 9, backgroundColor: '#fff'},
  urlGlyph: {color: '#95a29b', fontSize: 16},
  urlInput: {flex: 1, padding: 0, color: '#4d5a54', fontSize: 13},
  formError: {marginTop: 9, color: '#b86e60', fontSize: 11},
  modalSubmit: {marginTop: 12},
  modalFootnote: {marginTop: 15, color: '#afb6b1', fontSize: 10, textAlign: 'center'},
});
