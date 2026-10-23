using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace NewsQuack.App.Parsing;

/// <summary>
/// T-012: pure, culture-hostile date parser for feed items. Maps a raw date string to a UTC
/// <see cref="DateTimeOffset"/> with zero offset, or null when the value is missing/unparseable.
/// InvariantCulture only — never CurrentCulture (the de-DE smoke test guards this). An unparseable
/// or absent date yields null so the caller can drop the item; it is NEVER silently stamped "now".
/// </summary>
public sealed class FeedDateParser
{
    // RFC 822/1123: weekday optional, numeric ±hhmm offset (the form RSS pubDate uses). The general
    // TryParse below handles many of these already; the explicit patterns are the safety net for the
    // variants it misses (coordinator decision 3).
    private static readonly string[] Rfc822Patterns =
    [
        "ddd, dd MMM yyyy HH:mm:ss zzz",   // Tue, 21 Oct 2025 16:42:10 -0700
        "dd MMM yyyy HH:mm:ss zzz",         // 21 Oct 2025 16:42:10 -0700 (weekday omitted)
    ];

    // Date-only values (no time part) resolve to midnight UTC of that day — disambiguated later by
    // identity-based dedup, not date comparison (coordinator decision 3).
    private static readonly string[] DateOnlyPatterns = ["yyyy-MM-dd", "dd MMM yyyy"];

    // Trailing numeric offset with or without a colon: -0700 / +02:00. Used to detect "has tz info".
    private static readonly Regex NumericOffsetSuffix = new("[+-]\\d{2}:?\\d{2}$", RegexOptions.Compiled);

    private readonly ILogger<FeedDateParser>? _logger;

    public FeedDateParser(ILogger<FeedDateParser>? logger = null) => _logger = logger;

    /// <summary>
    /// Parses a raw feed date string into UTC (zero offset). Returns null for missing/empty input or
    /// anything that cannot be parsed — the caller drops the item rather than guessing.
    /// </summary>
    public DateTimeOffset? TryParse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var s = raw.Trim();

        // 1. General parse: ISO 8601 (Z / ±hh:mm / ±hhmm / fractional seconds), RFC 822 with offset,
    //    and date-only values. AssumeUniversal makes tz-less full times resolve as UTC; AdjustToUniversal
    //    normalizes every result to a zero offset.
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var general))
            return LogAndNormalize(general, s);

        // 2. RFC 822 explicit patterns for the variants the general parser misses.
        foreach (var pattern in Rfc822Patterns)
            if (DateTimeOffset.TryParseExact(s, pattern, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AllowWhiteSpaces, out var exact))
                return LogAndNormalize(exact, s);

        // 3. Date-only values → midnight UTC of that day.
        foreach (var pattern in DateOnlyPatterns)
            if (DateTime.TryParseExact(s, pattern, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateOnly))
                return LogAndNormalize(new DateTimeOffset(dateOnly.Date, TimeSpan.Zero), s);

        return null;   // unparseable ⇒ null ⇒ the item is dropped upstream (never "now")
    }

    private DateTimeOffset LogAndNormalize(DateTimeOffset value, string raw)
    {
        // Full time part present but no timezone information at all → we assumed UTC; make that observable.
        if (_logger is not null && !HasTimezoneInfo(raw) && raw.Contains(':'))
            _logger.LogDebug("Date '{RawDate}' carries no timezone information; assuming UTC", raw);

        return value.ToUniversalTime();   // zero offset (identity when already normalized — defensive)
    }

    private static bool HasTimezoneInfo(string s) =>
        s.EndsWith('Z') || s.EndsWith('z') || NumericOffsetSuffix.IsMatch(s);
}
