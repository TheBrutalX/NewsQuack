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

        Assert.Equal("Hello & welcome World's 'nice'", HtmlPlainText.Decode(html));
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
