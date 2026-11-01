namespace NewsQuack.App.Tests;

        /// <summary>
        /// T-042 (coordinator decision 5): the single reference table every matrix cell asserts against — the exact expected
        /// stdout lines for all six parsed documents (the five shared fixtures + the inline edge-case document), in parse order:
        /// <c>fixtureName \t itemId \t title \t publishedAt</c> with <c>publishedAt</c> in round-trip "O" format at zero offset.
        /// The fixture instants are the same UTC values T-012's FeedParserTests already assert; the edge-case instants were
        /// computed once from decision 3 and are fixed here. All six cells assert against this one table — a change to it is a
        /// conscious decision, not an accident.
        /// </summary>
public static class TzCellReferenceTable
{
    public static readonly string[] ExpectedLines =
    [
        // rss2.xml — RFC 822 pubDates in US/Pacific (PDT, -0700), incl. a local-midnight item and one crossing into the next UTC day.
        "rss2.xml\thttps://example.com/blog/compiler-release\tNew compiler release & performance notes\t2025-10-21T23:42:10.0000000+00:00",
        "rss2.xml\thttps://example.com/blog/rss-validation\tA practical guide to RSS validation\t2025-10-21T16:15:44.0000000+00:00",
        "rss2.xml\thttps://example.com/blog/midnight-deploy\tShipping at 00:05 <why not?>\t2025-10-21T07:05:12.0000000+00:00",
        "rss2.xml\thttps://example.com/blog/community-call-dedup\tCommunity call: Q&A on feed deduplication\t2025-10-21T01:30:00.0000000+00:00",
        // atom.xml — explicit UTC offsets in both 'Z' and '+02:00' forms, normalized to zero offset.
        "atom.xml\turn:uuid:1a2b3c4d-0001-4f5e-9a8b-7c6d5e4f3a2b\tShipping & testing in parallel\t2025-10-21T14:30:00.0000000+00:00",
        "atom.xml\turn:uuid:1a2b3c4d-0002-4f5e-9a8b-7c6d5e4f3a2b\tMigrating a legacy parser without downtime\t2025-10-21T11:58:12.0000000+00:00",
        "atom.xml\turn:uuid:1a2b3c4d-0003-4f5e-9a8b-7c6d5e4f3a2b\tPost-mortem: the feed that ate a weekend\t2025-10-20T09:15:44.0000000+00:00",
        "atom.xml\turn:uuid:1a2b3c4d-0004-4f5e-9a8b-7c6d5e4f3a2b\tNotes on at-least-once delivery\t2025-10-19T21:58:07.0000000+00:00",
        // youtube.xml — YouTube channel RSS shape, UTC (+0000) pubDates.
        "youtube.xml\ttag:youtube.com,2016:video:aB3dE5fGh7i\tCNC router build log #12 — gantry & rails\t2025-10-21T16:42:10.0000000+00:00",
        "youtube.xml\ttag:youtube.com,2016:video:jK9mL2nOp4q\tSoldering fume extraction on a budget\t2025-10-19T18:05:33.0000000+00:00",
        "youtube.xml\ttag:youtube.com,2016:video:rSt6uVwXyZ8\tWhy my first 3D printer frame failed (and the fix)\t2025-10-17T12:30:00.0000000+00:00",
        // reddit.xml — Reddit Atom shape, millisecond-precision Zulu dates preserved end-to-end.
        "reddit.xml\tt3_1mxyz9a\tShow /r/rust: I built a CLI in 500 lines of Rust in /r/rust\t2025-10-21T16:39:58.4560000+00:00",
        "reddit.xml\tt3_1mwq8zb\tTraits vs. enums for error handling in /r/rust\t2025-10-21T14:12:47.9010000+00:00",
        "reddit.xml\tt3_1mvn7ya\tRust for C++ developers: the first week in /r/rust\t2025-10-20T21:45:19.6640000+00:00",
        // gitlab.xml — GitLab Atom shape, Zulu dates.
        "gitlab.xml\ttag:gitlab.example.com,2025:acme/payments-service/commit/a3f8c9d2e4b1\tCommit a3f8c9d2e4b1 — Fix idempotency key collision on retries\t2025-10-21T16:40:07.0000000+00:00",
        "gitlab.xml\ttag:gitlab.example.com,2025:acme/payments-service/release/v2025.10.1\tRelease v2025.10.1\t2025-10-20T18:15:33.0000000+00:00",
        "gitlab.xml\ttag:gitlab.example.com,2025:acme/payments-service/commit/7b2e4f0a9c31\tCommit 7b2e4f0a9c31 — Add circuit breaker around the PSP client\t2025-10-19T11:02:56.0000000+00:00",
        // edge-case.xml (inline, decision 3) — back-dated item keeps its own older instant; identical timestamps both present at the
        // same instant; date-only stamps (ISO and RFC 822 without time) resolve to exact midnight-UTC instants, identical for same-day items.
        "edge-case.xml\tedge-a-backdated\tBack-dated archive post\t2025-10-14T15:15:30.0000000+00:00",
        "edge-case.xml\tedge-b-sibling-one\tSibling post one (identical timestamp)\t2025-10-21T16:30:00.0000000+00:00",
        "edge-case.xml\tedge-c-sibling-two\tSibling post two (identical timestamp)\t2025-10-21T16:30:00.0000000+00:00",
        "edge-case.xml\tedge-d-date-only-iso\tDate-only post (ISO)\t2025-10-21T00:00:00.0000000+00:00",
        "edge-case.xml\tedge-e-date-only-rfc822\tDate-only post (RFC 822)\t2025-10-21T00:00:00.0000000+00:00",
    ];
}
