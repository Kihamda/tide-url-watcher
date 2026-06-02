import {findFeedCandidates, normalizeUrl, parseFeed, parseWebsite, stripMarkup} from '../src/core/feed';

test('normalizes bare website URLs', () => {
  expect(normalizeUrl('example.com/news')).toBe('https://example.com/news');
});

test('extracts RSS stories', () => {
  const feed = parseFeed(
    '<rss><channel><title>Journal</title><item><title>A first note</title><link>/notes/1</link><description><![CDATA[<p>Quiet details.</p>]]></description></item></channel></rss>',
    'journal',
    'https://example.com/feed.xml',
  );
  expect(feed?.title).toBe('Journal');
  expect(feed?.stories[0]).toMatchObject({
    title: 'A first note',
    summary: 'Quiet details.',
    url: 'https://example.com/notes/1',
  });
});

test('extracts Atom stories', () => {
  const feed = parseFeed(
    '<feed><title>Notes</title><entry><title>New workspace</title><link href="/posts/2" /><summary>Small improvements.</summary><updated>2026-01-02T03:04:05Z</updated></entry></feed>',
    'notes',
    'https://example.com/atom.xml',
  );
  expect(feed?.stories[0]).toMatchObject({
    title: 'New workspace',
    url: 'https://example.com/posts/2',
    publishedAt: '2026-01-02T03:04:05Z',
  });
});

test('discovers feeds and falls back to article cards', () => {
  const html = `
    <html><head><link rel="alternate" type="application/rss+xml" href="/feed.xml"></head>
    <body><article><h2><a href="/journal/calm">A thoughtful first release</a></h2><p>Gentle details.</p></article></body></html>
  `;
  expect(findFeedCandidates(html, 'https://example.com')[0]).toBe('https://example.com/feed.xml');
  expect(parseWebsite(html, 'site', 'https://example.com')[0]).toMatchObject({
    title: 'A thoughtful first release',
    summary: 'Gentle details.',
    url: 'https://example.com/journal/calm',
  });
});

test('strips markup', () => {
  expect(stripMarkup('<p>Hello <strong>calm</strong> web.</p>')).toBe('Hello calm web.');
});
