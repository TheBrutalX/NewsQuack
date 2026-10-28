using NewsQuack.App.Discord;
using NewsQuack.App.Parsing;
using Xunit;

namespace NewsQuack.App.Tests;

/// <summary>
/// T-013: unit tests of the shared <see cref="HtmlPlainText"/> helper (the one decoding path used by every post builder).
/// The logic is a byte-identical move of T-020's DiscordPayloadBuilder.PlainText, so these tests pin both the original
/// stripping behavior and the new optional limit.
/// </summary>
public class HtmlPlainTextTests
{
    [Fact]
    public void HtmlPlainText_EntitiesNestedTagsAndWhitespace_CollapsedToPlainText()
    {
        const string html = "<p>Hello &amp; welcome</p>\n\n<b>World&#39;s &#x27;nice&#x27;</b>";

        // T-055: the literal \n\n is now one preserved blank line, not a collapsed space (intra-line runs still collapse).
        Assert.Equal("Hello & welcome\n\nWorld's 'nice'", HtmlPlainText.Decode(html));
    }

    [Fact]
    public void HtmlPlainText_BrVariantsCaseInsensitive_EachBecomesNewline()
    {
        // Every <br> form — any case, any internal whitespace, optional slash (incl. the spaced "< br >") — becomes exactly one \n.
        var cases = new[]
        {
            ("x<br>y", "x\ny"),
            ("<a>x</a><br>y", "x\ny"),       // adjacent tags still become spaces; only <br> becomes a newline
            ("x<BR/>y", "x\ny"),
            ("x<br />y", "x\ny"),
            ("x < br > y", "x\ny"),          // whitespace inside the tag is tolerated too
            ("X <Br/> Y", "X\nY"),           // mixed case + spaces around the text
        };

        foreach (var (input, expected) in cases)
        {
            var decoded = HtmlPlainText.Decode(input);

            Assert.Equal(expected, decoded);                        // newline at exactly the <br> position…
            Assert.DoesNotContain("<br", decoded!.ToLowerInvariant());   // …and no residual <br text survives
        }
    }

    [Fact]
    public void HtmlPlainText_CvefeedCanonicalSample_MultiLineSectionsWithBlankLines()
    {
        // The owner's reference feed (issue #71): labelled sections separated by <br /> and literal newlines, pinned byte-for-byte.
        const string html = "\n  <strong>CVE ID :</strong>CVE-2026-78674\n  <br />\n  <strong>Published :</strong> Mercoledì 28 Ottobre 2026 19:21 | 28 minuti ago\n  <br />\n  <strong>Description :</strong>The BPMN System REST APIs allow an attacker to bypass access controls.\n\nAn authenticated user can leverage this flaw to read arbitrary data.\n--\n  Visit the link for more details about this vulnerability.";

        const string expected = "CVE ID : CVE-2026-78674\n\nPublished : Mercoledì 28 Ottobre 2026 19:21 | 28 minuti ago\n\nDescription : The BPMN System REST APIs allow an attacker to bypass access controls.\n\nAn authenticated user can leverage this flaw to read arbitrary data.\n--\nVisit the link for more details about this vulnerability.";

        Assert.Equal(expected, HtmlPlainText.Decode(html));
    }

    [Fact]
    public void HtmlPlainText_ThreePlusConsecutiveNewlines_CollapseToOneBlankLine()
    {
        // 3+ consecutive newlines (incl. whitespace-only lines between them) collapse to exactly one blank line…
        Assert.Equal("a\n\nb", HtmlPlainText.Decode("a\n\n\n\nb"));
        Assert.Equal("a\n\nb", HtmlPlainText.Decode("a \n\t\n \n b"));

        // …but a single blank line (exactly two newlines) is preserved as-is — never collapsed further.
        Assert.Equal("a\n\nb", HtmlPlainText.Decode("a\n\nb"));
    }

    [Fact]
    public void HtmlPlainText_NoNewlineNoBrInput_IsByteIdenticalToFlatten()
    {
        // Regression guard (T-055): inputs with no newlines and no <br> must render byte-identically to the old whole-string collapse.
        Assert.Equal(
            "The quick brown fox & the lazy dog jump over fences repeatedly",
            HtmlPlainText.Decode("<p>The quick brown fox &amp; the lazy dog jump over fences repeatedly</p>"));

        // Intra-line runs (incl. &nbsp;/U+00A0, which Regex \s treats as whitespace) still collapse to a single space.
        Assert.Equal("a b c", HtmlPlainText.Decode("a&nbsp;b &#160; c"));

        // Tag-heavy one-liner: adjacent tags + literal spaces collapse to exactly one space between words.
        Assert.Equal("Deep <nested> & bold", HtmlPlainText.Decode("<div><p>Deep &lt;nested&gt; &amp; <b>bold</b></p></div>"));
    }

    [Fact]
    public void HtmlPlainText_MaxCharsPrefixCutOnMultiLine_KeepsExactPrefixSemantics()
    {
        const string html = "<p>First line</p><br/>Second line with <b>bold</b> tail<br/>Third";
        var unlimited = HtmlPlainText.Decode(html)!;

        Assert.Equal("First line\nSecond line with bold tail\nThird", unlimited);

        var cutPoint = 25;   // lands mid-line, inside "Second line …"
        var limited = HtmlPlainText.Decode(html, cutPoint);

        Assert.NotNull(limited);
        Assert.Equal(cutPoint, limited!.Length);    // exactly maxChars — plain character cut on the normalised output…
        Assert.Equal(unlimited[..cutPoint], limited);   // …an exact prefix of the unlimited output (no ellipsis at parse level)

        Assert.Equal(unlimited, HtmlPlainText.Decode(html, unlimited.Length));   // limit ≥ length ⇒ identical to unlimited
    }

    [Fact]
    public void HtmlPlainText_DoubleDecodeWithBrAndNewlines_IsIdempotentIncludingDiscordDelegation()
    {
        // T-055: the double-decode path (mapper decodes at parse; DiscordPayloadBuilder.PlainText decodes again) must stay
        // idempotent under the new line-preserving rules for representative multi-line/<br> inputs.
        var inputs = new[]
        {
            "Line A<br/>Line B\n\nLine C",
            "<p>Section one</p>\n  <br />\n<p>Section two &amp; more</p>",
            "a&nbsp;b<br/>c &#39;d&#x27;\n\ne f",
        };

        foreach (var input in inputs)
        {
            var once = HtmlPlainText.Decode(input);
            Assert.NotNull(once);
            Assert.Equal(once, HtmlPlainText.Decode(once));   // idempotent at the helper level…

            // …and through the public Discord entry point that delegates to the same helper.
            var viaDiscord = DiscordPayloadBuilder.PlainText(input);
            Assert.Equal(once, viaDiscord);
            Assert.Equal(viaDiscord, DiscordPayloadBuilder.PlainText(viaDiscord));
        }
    }

    [Fact]
    public void HtmlPlainText_EntityDecodingInsideMultiLineText_StillDecodesPerLine()
    {
        // Entities and numeric refs on different lines all decode; the <br>-introduced newline is preserved between them.
        Assert.Equal("a & b\nc 'd'", HtmlPlainText.Decode("a &amp; b<br/>c &#39;d&#x27;"));

        // …including hex refs and nbsp (which collapses like any other intra-line whitespace run).
        Assert.Equal("x y\nz<w>", HtmlPlainText.Decode("x&nbsp;y<br/>z&#60;w&#x3E;"));
    }

    [Fact]
    public void HtmlPlainText_NullOrEmpty_ReturnsNull()
    {
        Assert.Null(HtmlPlainText.Decode(null));
        Assert.Null(HtmlPlainText.Decode(string.Empty));
        Assert.Null(HtmlPlainText.Decode("   \n\t "));       // whitespace only ⇒ empty after decoding
        Assert.Null(HtmlPlainText.Decode("<div></div>"));    // tags only ⇒ empty after stripping
    }

    [Fact]
    public void HtmlPlainText_LimitSet_IsExactPrefixOfUnlimited()
    {
        const string html = "<p>The quick brown fox &amp; the lazy dog jump over fences repeatedly</p>";
        var unlimited = HtmlPlainText.Decode(html)!;   // "The quick brown fox & the lazy dog jump over fences repeatedly"

        var limited = HtmlPlainText.Decode(html, 20);

        Assert.NotNull(limited);
        Assert.Equal(20, limited!.Length);                                  // exactly maxChars — plain character cut, no ellipsis
        Assert.Equal(unlimited[..20], limited);                             // exact prefix of the unlimited output
        Assert.Equal(unlimited, HtmlPlainText.Decode(html, unlimited.Length));   // limit ≥ length ⇒ identical to unlimited
        Assert.Null(HtmlPlainText.Decode(html, 0));                         // cut to zero characters ⇒ empty after decoding ⇒ null
    }

    [Fact]
    public void HtmlPlainText_AppliedTwice_IsIdempotentOnRepresentativeInputs()
    {
        // Representative decoded content: entities that decode to non-tag characters (so a second pass finds nothing new).
        var inputs = new[]
        {
            "Tom &amp; Jerry's &#x27;adventures&#x27; end",
            "<p>Line one</p>\n\n<p>Line two</p>",
            "a&nbsp;b &#160; c",
            "&#65;&#x42; &amp;",
        };

        foreach (var input in inputs)
        {
            var once = HtmlPlainText.Decode(input);
            Assert.NotNull(once);
            Assert.Equal(once, HtmlPlainText.Decode(once));                 // idempotent: decoding already-decoded text changes nothing

            // The public Discord entry point delegates to the same helper — no double-decoding regression on decoded text.
            var viaDiscord = DiscordPayloadBuilder.PlainText(input);
            Assert.Equal(once, viaDiscord);
            Assert.Equal(viaDiscord, DiscordPayloadBuilder.PlainText(viaDiscord));
        }
    }

    [Fact]
    public void HtmlPlainText_MalformedNumericRefAndUnknownEntity_KeptAsIs()
    {
        // Malformed numeric refs and unknown named entities pass through unchanged — never crash stripping (T-020 behavior).
        Assert.Equal("bad &#x; ref &somecustom; kept", HtmlPlainText.Decode("bad &#x; ref &somecustom; kept"));

        // Surrogate code points alone are invalid — the raw entity is kept.
        Assert.Equal("surrogate &#55333; kept", HtmlPlainText.Decode("surrogate &#55333; kept"));
    }
}
