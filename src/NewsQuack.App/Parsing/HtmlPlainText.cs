using System.Globalization;
using System.Text.RegularExpressions;

namespace NewsQuack.App.Parsing;

/// <summary>
/// T-013 (amended by T-055): the single shared HTML → plain-text helper used by every post builder (and, via delegation, by
/// the Discord payload builder). Moved as-is from T-020's <c>DiscordPayloadBuilder.PlainText</c> so all branches decode
/// coherently. Turns <c>&lt;br&gt;</c> variants into newlines (before the generic tag strip), removes tags (replaced with a
/// space so adjacent words do not merge), decodes common entities and numeric character references, then preserves line
/// structure: normalizes CRLF/CR to LF, collapses intra-line whitespace runs to a single space (each line trimmed at both
/// ends), collapses any run of 3+ consecutive newlines to exactly one blank line, and trims the result at both ends.
/// Inputs with no newlines and no <c>&lt;br&gt;</c> render byte-identically to the original whole-string collapse
/// (T-055 regression guard). Returns null when the result is empty. The optional <paramref name="maxChars"/> limit is
/// applied AFTER normalization as a plain character cut (no ellipsis at parse level) — the limited output is always an exact
/// prefix of the unlimited one.
/// </summary>
internal static class HtmlPlainText
{
    // <br>/<br/>/<BR />/… (any case, any internal whitespace, optional slash) → newline; never matches <brand>.
    private static readonly Regex BrPattern = new(@"<\s*br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TagPattern = new("<[^>]*>", RegexOptions.Compiled);
    private static readonly Regex EntityPattern = new("&#x?[0-9a-fA-F]+;|&[a-zA-Z][a-zA-Z0-9]*;", RegexOptions.Compiled);
    private static readonly Regex WhitespacePattern = new("\\s+", RegexOptions.Compiled);
    private static readonly Regex NewlinePattern = new("\r\n?|\r", RegexOptions.Compiled);   // CRLF / lone CR → LF
    private static readonly Regex BlankRunPattern = new("\n{3,}", RegexOptions.Compiled);    // 3+ newlines → one blank line

    public static string? Decode(string? html, int? maxChars = null)
    {
        if (string.IsNullOrEmpty(html))
            return null;

        var lfOnly = NewlinePattern.Replace(html, "\n");          // treat \n as the only newline (CRLF/CR feeds behave like LF)
        var brAsNewlines = BrPattern.Replace(lfOnly, "\n");       // <br>-forms become real newlines, BEFORE the generic tag strip
        var noTags = TagPattern.Replace(brAsNewlines, " ");
        var decoded = EntityPattern.Replace(noTags, DecodeEntity);

        // Line-aware normalisation (T-055): per-line \s collapse + trim (exact Regex \s semantics — &nbsp;/U+00A0 included),
        // then 3+ consecutive newlines → exactly one blank line. For a no-newline input this is byte-identical to the old
        // whole-string "\s+"→" " + trim.
        var lines = decoded.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            lines[i] = WhitespacePattern.Replace(lines[i], " ").Trim();
        var normalized = BlankRunPattern.Replace(string.Join("\n", lines), "\n\n").Trim();

        if (normalized.Length == 0)
            return null;

        // Optional limit: plain character cut after normalization — the result is an exact prefix of the unlimited output.
        var result = maxChars is { } limit && normalized.Length > limit
            ? normalized[..Math.Clamp(limit, 0, normalized.Length)]
            : normalized;

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
