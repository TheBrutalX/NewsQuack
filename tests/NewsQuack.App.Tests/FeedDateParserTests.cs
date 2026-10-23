using NewsQuack.App.Parsing;
using Xunit;

namespace NewsQuack.App.Tests;

/// <summary>
/// T-012: pure unit matrix for <see cref="FeedDateParser"/>. No fixtures, no DI — the type is exercised directly with a
/// null logger so it stays a pure function of its input. Expected values are exact UTC instants (zero offset).
/// </summary>
public class FeedDateParserTests
{
    private readonly FeedDateParser _parser = new();   // null logger ⇒ pure

    [Fact]
    public void Rfc822WithNumericOffset_ReturnsExactUtc() =>
        Assert.Equal(Utc(2025, 10, 21, 23, 42, 10), _parser.TryParse("Tue, 21 Oct 2025 16:42:10 -0700"));

    [Fact]
    public void Rfc822WithoutWeekday_ReturnsExactUtc() =>
        Assert.Equal(Utc(2025, 10, 21, 23, 42, 10), _parser.TryParse("21 Oct 2025 16:42:10 -0700"));

    [Fact]
    public void Iso8601ZuluAndOffsetAndFractional_ReturnExactUtc()
    {
        // Zulu form.
        Assert.Equal(Utc(2025, 10, 21, 14, 30, 0), _parser.TryParse("2025-10-21T14:30:00Z"));

        // Explicit +hh:mm offset (subtracts the offset to reach UTC).
        Assert.Equal(Utc(2025, 10, 21, 11, 58, 12), _parser.TryParse("2025-10-21T13:58:12+02:00"));

        // ISO compact ±hhmm offset (no colon).
        Assert.Equal(Utc(2025, 10, 21, 9, 42, 10), _parser.TryParse("2025-10-21T16:42:10+0700"));

        // Fractional seconds preserved.
        Assert.Equal(Utc(2025, 10, 21, 16, 39, 58, ms: 456), _parser.TryParse("2025-10-21T16:39:58.456Z"));
    }

    [Fact]
    public void DateOnlyString_ReturnsMidnightUtc()
    {
        Assert.Equal(Utc(2025, 10, 21), _parser.TryParse("2025-10-21"));       // ISO date-only
        Assert.Equal(Utc(2025, 10, 21), _parser.TryParse("21 Oct 2025"));      // RFC-style date-only
    }

    [Fact]
    public void NoTimezoneFullTime_AssumedUtc()
    {
        var result = _parser.TryParse("2025-10-21T14:30:00");   // full time, no tz info at all

        Assert.Equal(Utc(2025, 10, 21, 14, 30, 0), result);
        Assert.Equal(TimeSpan.Zero, result!.Value.Offset);       // explicitly zero offset (UTC)
    }

    [Theory]
    [InlineData("not-a-date")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("21 Oct 9999 25:61:61 -0700")]   // out-of-range time components are not a valid date
    public void GarbageOrEmpty_ReturnsNull(string? input) => Assert.Null(_parser.TryParse(input));

    [Fact]
    public void NullInput_ReturnsNull() => Assert.Null(_parser.TryParse(null));

    private static DateTimeOffset Utc(int y, int mo, int d, int h = 0, int mi = 0, int s = 0, int ms = 0) =>
        new(new DateTime(y, mo, d, h, mi, s).AddMilliseconds(ms), TimeSpan.Zero);
}
