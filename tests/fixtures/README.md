# Feed fixtures

Shared XML feed fixtures consumed verbatim by the parsing/builder test tasks (and by any later
integration test that needs a realistic feed document). All files are valid UTF-8, well-formed
XML *except* where a fixture explicitly encodes a defect, and small (< 20 KB).

Load them through `FixtureLoader` in the test projects (resolves paths relative to the solution
root — walk up from the test assembly location until `NewsQuack.sln` is found) instead of
hard-coding paths.

## Well-formed fixtures

| File | Format | Purpose / encoded content |
|---|---|---|
| `rss2.xml` | RSS 2.0 | Channel with title/link/description/language; 4 items with title, link, guid and RFC 822 `pubDate` in US/Pacific (PDT, `-0700`) — including one near-midnight local time (`00:05`). Descriptions contain HTML entities and inline `<img>` tags; authors come from the `dc:creator` namespace. |
| `rss2-multiline-desc.xml` | RSS 2.0 | One item whose `<description>` is the canonical cvefeed.io sample (T-055): labelled sections separated by `<br />` tags and literal newlines, XML-escaped exactly as a real feed carries them. Pins line-structure preservation through the real parse path. |
| `atom.xml` | Atom | 4 entries with title, link href, id, and explicit UTC offsets in **both** `Z` and `+02:00` forms (feed-level and entry-level). `content type="html"` with entities; feed- and entry-level author name/uri. |
| `youtube.xml` | YouTube channel RSS | The exact shape YouTube serves: `xmlns:yt`, per-item `<media:group>` with `<media:title>`, `<media:description>`, `<media:community><media:statistics views=...>`, `<media:thumbnail url=...>`, UTC (`+0000`) pubDate, `tag:youtube.com` GUIDs and `yt:videoId`. 3 entries. |
| `reddit.xml` | Reddit subreddit Atom (the variant Reddit serves at `/r/<sub>/feed`) | Reddit-specific ids (`t5_…`/`t3_…`), `<category>` tags, authors as `/u/name`, alternate + replies links to the post/comment pages, millisecond-precision Zulu dates. 3 entries. |
| `gitlab.xml` | GitLab project Atom feed | Project title; one entry per commit/release with titles like `Commit <sha>`, links to the commit page, author name/uri, published/updated. 3 entries (2 commits + 1 release). |

## Broken variants (each breaks exactly the stated thing and nothing else)

| File | Defect encoded |
|---|---|
| `rss2-broken-item.xml` | RSS 2.0 where **exactly one** item (the third) is malformed — it has no `<title>`. The other three items are valid and parseable; the document itself is well-formed XML. A per-item-error-isolated parser must drop exactly that one item. |
| `atom-broken-date.xml` | Atom where **exactly one** entry (the second) has an unparseable date (`<updated>not-a-date</updated>` with no `<published>`). The other entries carry valid dates; the document is well-formed XML. A parser must drop exactly that one entry (no silent fallback to "now"). |
| `feed-malformed.xml` | 100% unusable: the document is truncated mid-tag, so it does not parse as XML at all. Represents a fully broken feed (whole-feed error, not per-item). |

## Rules for future changes

- Keep fixtures standard and well-formed except where explicitly broken; "broken" variants must
  break exactly the stated thing and nothing else.
- Dates must remain parseable in the well-formed fixtures (explicit offsets, no bare local times).
- Keep file sizes small (< 20 KB) so they stay readable inline in tests and diffs.
