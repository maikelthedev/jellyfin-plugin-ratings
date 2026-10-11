'use strict';

/**
 * A vertical drama is reviewed as a whole, so the rating is stored on its SEASON (see
 * ResolveRatingTarget on the server). A season carries no artwork of its own - Jellyfin 404s
 * /Items/<season>/Images/Primary - so any renderer that builds a poster URL out of the stored
 * item id shows an empty card instead of the show's poster.
 *
 * Every profile renderer therefore has to use the poster the API resolved for it (PosterUrl),
 * which points at the show, and must not invent an /Items/<stored id> URL of its own.
 */

const test = require('node:test');
const assert = require('node:assert');
const { loadPlugin } = require('./support/harness');

const SEASON = 'a'.repeat(32);
const SERIES = 'b'.repeat(32);
const USER = '11111111111111111111111111111111';

/** The artwork URL shape the plugin builds for a Jellyfin item. */
const posterFor = (id) => '/Items/' + id + '/Images/Primary';

/** A rating as the API sends it for a vertical show: stored on the season, poster on the show. */
function seasonRating(extra) {
    return Object.assign({
        ItemId: SEASON,
        ItemName: 'Season 1: A Show',
        Rating: 8,
        ReviewText: 'It is very good',
        InLibrary: true,
        PosterUrl: posterFor(SERIES)
    }, extra || {});
}

function loadProfile(ratings) {
    const harness = loadPlugin({ responses: { '/Ratings/Users/': ratings } });
    harness.plugin._viewingProfileUserId = USER;
    return harness;
}

/** Let the plugin's fetch → .then chain settle. */
const settle = () => new Promise((resolve) => setImmediate(resolve));

function assertUsesShowArtwork(el) {
    assert.ok(el.innerHTML.includes(posterFor(SERIES)), 'should ask for the show artwork, got: ' + el.innerHTML);
    assert.ok(!el.innerHTML.includes(posterFor(SEASON)), 'should not ask for the season artwork, got: ' + el.innerHTML);
}

test('overview posters use the API poster, not one guessed from the stored season id', async () => {
    const harness = loadProfile([seasonRating()]);
    const el = harness.document.__set('lbRecentPosters');

    harness.plugin.loadProfileRecentPosters();
    await settle();
    await settle();

    assertUsesShowArtwork(el);
});

test('overview review thumbnails use the API poster, not the stored season id', async () => {
    const harness = loadProfile([seasonRating()]);
    const el = harness.document.__set('lbRecentReviews');

    harness.plugin.loadProfileRecentReviews();
    await settle();
    await settle();

    assertUsesShowArtwork(el);
});

test('the Reviews tab uses the API poster, not the stored season id', async () => {
    const harness = loadProfile([seasonRating()]);
    const el = harness.document.__set('lbProfileContent');

    harness.plugin.renderProfileReviewsTab();
    await settle();
    await settle();

    assertUsesShowArtwork(el);
});

test('the Ratings tab uses the API poster, not the stored season id', async () => {
    const harness = loadProfile([seasonRating()]);
    const el = harness.document.__set('lbProfileContent');

    harness.plugin.renderProfileRatingsTab();
    await settle();
    await settle();

    assertUsesShowArtwork(el);
});

test('a catalog title with no library item behind it keeps its stored poster', async () => {
    const harness = loadProfile([seasonRating({
        ItemId: '',
        InLibrary: false,
        PosterUrl: 'https://image.tmdb.org/t/p/w300/abc.jpg'
    })]);
    const el = harness.document.__set('lbProfileContent');

    harness.plugin.renderProfileReviewsTab();
    await settle();
    await settle();

    assert.ok(el.innerHTML.includes('image.tmdb.org'), 'should keep the stored poster, got: ' + el.innerHTML);
});

/**
 * The renderers share one URL builder, so its rules are pinned directly here: the server's poster
 * wins, a server path keeps the resize hint the layout asked for, and a stored external poster
 * (TMDB) is used untouched because it takes no such parameter.
 */
test('a server-resolved poster keeps the size the layout asked for', () => {
    const { plugin } = loadPlugin();
    const url = plugin.imageUrlFor({ PosterUrl: posterFor(SERIES), ItemId: SEASON, InLibrary: true }, 'https://jellyfin.test', 240);
    assert.strictEqual(url, 'https://jellyfin.test' + posterFor(SERIES) + '?maxHeight=240');
});

test('a stored external poster is used as it is, with no size parameter', () => {
    const { plugin } = loadPlugin();
    const url = plugin.imageUrlFor({ PosterUrl: 'https://image.tmdb.org/t/p/w300/a.jpg', InLibrary: false }, 'https://jellyfin.test', 240);
    assert.strictEqual(url, 'https://image.tmdb.org/t/p/w300/a.jpg');
});

test('with no stored poster it falls back to the library item at the size asked for', () => {
    const { plugin } = loadPlugin();
    const url = plugin.imageUrlFor({ ItemId: SERIES, InLibrary: true }, 'https://jellyfin.test', 120);
    assert.strictEqual(url, 'https://jellyfin.test' + posterFor(SERIES) + '?maxHeight=120');
});
