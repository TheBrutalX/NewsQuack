using System.Globalization;
using System.Text.RegularExpressions;

namespace NewsQuack.App.Parsing;

/// <summary>
/// T-013: the single shared HTML → plain-text helper used by every post builder (and, via delegation, by the Discord
/// payload builder). Moved as-is from T-020's <c>DiscordPayloadBuilder.PlainText</c> so all branches decode coherently:
/// removes tags (replaced with a space so adjacent words do not merge), decodes common entities and numeric character
/// references, then collapses all whitespace runs to a single space. Returns null when the result is empty.
/// The optional <paramref name="maxChars"/> limit is applied AFTER decoding as a plain character cut (no ellipsis at parse
/// level) — the limited output is always an exact prefix of the unlimited one.
/// </summary>
internal static class HtmlPlainText
{
    private static readonly Regex TagPattern = new("<[^>]*>", RegexOptions.Compiled);
    private static readonly Regex EntityPattern = new("&#x?[0-9a-fA-F]+;|&[a-zA-Z][a-zA-Z0-9]*;", RegexOptions.Compiled);
    private static readonly Regex WhitespacePattern = new("\\s+", RegexOptions.Compiled);

    public static string? Decode(string? html, int? maxChars = null)
    {
        if (string.IsNullOrEmpty(html))
            return null;

        var noTags = TagPattern.Replace(html, " ");
        var decoded = EntityPattern.Replace(noTags, DecodeEntity);
        var collapsed = WhitespacePattern.Replace(decoded, " ").Trim();

        if (collapsed.Length == 0)
            return null;

        // Optional limit: plain character cut after decoding — the result is an exact prefix of the unlimited output.
        var result = maxChars is { } limit && collapsed.Length > limit
            ? collapsed[..Math.Clamp(limit, 0, collapsed.Length)]
            : collapsed;

        return result.Length == 0 ? null : result;   // a cut to zero characters is "empty after decoding" ⇒ null
    }

    private static string DecodeEntity(Match match)
    {
        var entity = match.Value;   // e.g. &amp;, &#39;, &#x27;

        if (entity[1] == '#')       // numeric character reference: &#39; / &#x27;
        {
            var digits = entity[2..^1];
            var isHex = digits.Length > 1 && (digits[0] is 'x' or 'X');
            var valuePart = isHex ? digits[1..] : digits;

            if ((isHex
                    ? int.TryParse(valuePart, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var codePoint)
                    : int.TryParse(valuePart, NumberStyles.Integer, CultureInfo.InvariantCulture, out codePoint))
                && codePoint > 0 && codePoint <= 0x10FFFF
                && (codePoint < 0xD800 || codePoint > 0xDFFF))   // surrogates alone are invalid — keep the raw entity
            {
                return char.ConvertFromUtf32(codePoint);
            }

            return entity;        // malformed numeric ref — keep as-is, never crash stripping
        }

        return entity.ToLowerInvariant() switch    // common named entities; unknown ones (e.g. &somecustom;) pass through unchanged
        {
            "&amp;" => "&",
            "&lt;" => "<",
            "&gt;" => ">",
            "&quot;" => "\"",
            "&apos;" => "'",
            "&nbsp;" => " ",   // collapses to a single space like every other whitespace run downstream
            _ => entity,
        };
    }
}
