using System.Globalization;
using System.Xml;
using Xunit;

namespace NewsQuack.Core.Tests;

/// <summary>
/// One sanity test per fixture file: well-formed fixtures must parse as XML and encode exactly the
/// documented content (counts, dates, authors); feed-malformed must not parse at all.
/// </summary>
public class FixtureSanityTests
{
    private static readonly string[] DateElementNames = ["pubDate", "published", "updated"];

    [Fact]
    public void Fixture_Rss2_IsWellFormedXml()
    {
        var xml = FixtureLoader.Load("rss2.xml");

        Assert.True(IsWellFormed(xml), "rss2.xml must be well-formed XML");

        var titles = ItemTitles(xml);
        Assert.True(titles.Count >= 4, $"rss2.xml needs at least 4 items, found {titles.Count}");
        Assert.All(titles, t => Assert.NotNull(t));   // every item has a title

        var dates = DateTimes(xml);
        Assert.NotEmpty(dates);
        // The near-midnight item: wall-clock 00:05 in US/Pacific (offset -07:00) as written in the document.
        Assert.Contains(dates, d => d.Offset == TimeSpan.FromHours(-7) && d.Hour == 0 && d.Minute == 5);
    }

    [Fact]
    public void Fixture_Atom_IsWellFormedXml()
    {
        var xml = FixtureLoader.Load("atom.xml");

        Assert.True(IsWellFormed(xml), "atom.xml must be well-formed XML");

        Assert.True(CountElements(xml, "entry") >= 4, "atom.xml needs at least 4 entries");
        Assert.True(CountElements(xml, "id") >= 5, "feed + every entry need an id");

        var dateTexts = ElementTexts(xml, "published", "updated");
        Assert.Contains(dateTexts, t => t.EndsWith("Z"));          // Zulu form as written
        Assert.Contains(dateTexts, t => t.Contains("+02:00"));     // explicit non-Z offset form as written

        var dates = DateTimes(xml);
        Assert.NotEmpty(dates);
    }

    [Fact]
    public void Fixture_YouTube_IsWellFormedXml()
    {
        var xml = FixtureLoader.Load("youtube.xml");

        Assert.True(IsWellFormed(xml), "youtube.xml must be well-formed XML");

        Assert.True(CountElements(xml, "item") >= 3, "youtube.xml needs at least 3 items");
        Assert.True(CountElements(xml, "videoId") >= 3, "every item needs a yt:videoId");
        Assert.True(CountElementsWithAttribute(xml, "statistics", "views") >= 3, "every media:group needs media:statistics views=...");
        Assert.Contains("xmlns:yt", xml);

        var dates = DateTimes(xml);
        Assert.NotEmpty(dates);
    }

    [Fact]
    public void Fixture_Reddit_IsWellFormedXml()
    {
        var xml = FixtureLoader.Load("reddit.xml");

        Assert.True(IsWellFormed(xml), "reddit.xml must be well-formed XML");

        Assert.True(CountElements(xml, "entry") >= 3, "reddit.xml needs at least 3 entries");
        Assert.All(ElementTexts(xml, "name"), n => Assert.StartsWith("/u/", n));   // authors are /u/name
        Assert.Contains("#comments", xml);                                // links to comment pages

        var dates = DateTimes(xml);
        Assert.NotEmpty(dates);
    }

    [Fact]
    public void Fixture_GitLab_IsWellFormedXml()
    {
        var xml = FixtureLoader.Load("gitlab.xml");

        Assert.True(IsWellFormed(xml), "gitlab.xml must be well-formed XML");

        Assert.True(CountElements(xml, "entry") >= 3, "gitlab.xml needs at least 3 entries");
        var titles = EntryTitles(xml);
        Assert.All(titles, t =>
            Assert.True((t ?? string.Empty).Contains("Commit") || (t ?? string.Empty).Contains("Release"),
                $"unexpected entry title: {t}"));

        var dates = DateTimes(xml);
        Assert.NotEmpty(dates);
    }

    [Fact]
    public void Fixture_Rss2BrokenItem_IsWellFormedXml()
    {
        var xml = FixtureLoader.Load("rss2-broken-item.xml");

        Assert.True(IsWellFormed(xml), "rss2-broken-item.xml must be well-formed XML (the defect is semantic, not structural)");

        var titles = ItemTitles(xml);
        Assert.Equal(4, titles.Count);
        Assert.Single(titles, t => t is null);   // exactly one item lacks a <title>
    }

    [Fact]
    public void Fixture_AtomBrokenDate_IsWellFormedXml()
    {
        var xml = FixtureLoader.Load("atom-broken-date.xml");

        Assert.True(IsWellFormed(xml), "atom-broken-date.xml must be well-formed XML (the defect is semantic, not structural)");

        var entriesWithValidDate = EntryHasValidDate(xml);
        Assert.Equal(3, entriesWithValidDate.Count);
        Assert.Single(entriesWithValidDate, valid => !valid);   // exactly one entry has an unparseable/missing date
    }

    [Fact]
    public void Fixture_FeedMalformed_DoesNotParseAsXml()
    {
        var xml = FixtureLoader.Load("feed-malformed.xml");

        Assert.False(IsWellFormed(xml), "feed-malformed.xml must NOT parse as XML (truncated document)");
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private static bool IsWellFormed(string xml)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml));
            while (reader.Read()) { }
            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private static int CountElements(string xml, string localName)
    {
        var count = 0;
        using var reader = XmlReader.Create(new StringReader(xml));
        while (reader.Read())
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == localName)
                count++;   // content is NOT consumed — works for container elements too
        return count;
    }

    /// <summary>Text of every element with one of the given local names. Only valid for leaf elements.</summary>
    private static List<string> ElementTexts(string xml, params string[] localNames)
    {
        var texts = new List<string>();
        using var reader = XmlReader.Create(new StringReader(xml));
        while (reader.Read())
            if (reader.NodeType == XmlNodeType.Element && localNames.Contains(reader.LocalName))
                texts.Add(reader.ReadElementContentAsString());
        return texts;
    }

    private static int CountElementsWithAttribute(string xml, string localName, string attributeName)
    {
        var count = 0;
        using var reader = XmlReader.Create(new StringReader(xml));
        while (reader.Read())
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == localName && reader.GetAttribute(attributeName) is not null)
                count++;
        return count;
    }

    /// <summary>One entry per &lt;item&gt;: its title text, or null when the item has no direct &lt;title&gt; child.</summary>
    private static List<string?> ItemTitles(string xml) => DirectChildTexts(xml, "item", "title");

    /// <summary>One entry per &lt;entry&gt;: its title text (Atom entries always carry one in these fixtures).</summary>
    private static List<string?> EntryTitles(string xml) => DirectChildTexts(xml, "entry", "title");

    private static List<string?> DirectChildTexts(string xml, string containerLocalName, string childLocalName)
    {
        var results = new List<string?>();
        int? childDepth = null;
        using var reader = XmlReader.Create(new StringReader(xml));
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;

            if (reader.LocalName == containerLocalName)
            {
                results.Add(null);
                childDepth = reader.Depth + 1;   // direct children only — skips channel/feed-level elements
            }
            else if (childDepth is int d && reader.LocalName == childLocalName && reader.Depth == d)
            {
                results[^1] = reader.ReadElementContentAsString();
            }
        }

        return results;
    }

    /// <summary>All parseable dates in the fixture (unparseable values are dropped); offsets are kept as written.</summary>
    private static List<DateTimeOffset> DateTimes(string xml)
    {
        var dates = new List<DateTimeOffset>();
        foreach (var text in ElementTexts(xml, DateElementNames))
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                dates.Add(parsed);
        return dates;
    }

    /// <summary>One bool per &lt;entry&gt;: true when at least one direct published/updated child parses as a date.</summary>
    private static List<bool> EntryHasValidDate(string xml)
    {
        var results = new List<bool>();
        int? childDepth = null;
        using var reader = XmlReader.Create(new StringReader(xml));
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;

            if (reader.LocalName == "entry")
            {
                results.Add(false);
                childDepth = reader.Depth + 1;
            }
            else if (childDepth is int d && DateElementNames.Contains(reader.LocalName) && reader.Depth == d)
            {
                var text = reader.ReadElementContentAsString();
                if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out _))
                    results[^1] = true;
            }
        }

        return results;
    }
}
