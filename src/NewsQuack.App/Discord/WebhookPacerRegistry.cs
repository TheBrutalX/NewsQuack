using System.Collections.Concurrent;   // ConcurrentDictionary — pacers for URLs discovered at runtime (e.g. the dead-letter webhook) are created on demand
using NewsQuack.App.Configuration;     // NewsQuackConfig / DiscordTargetConfig — the registry is built from the validated config at composition time

namespace NewsQuack.App.Discord;

/// <summary>
/// T-048: the shared per-webhook-URL pacer registry (task constraint 3) — built once at composition time from the full validated config, same pattern as
/// <c>SendQueueRegistry</c>. Pacing state is keyed by the NORMALIZED webhook URL (<see cref="Uri.AbsoluteUri"/> form, Ordinal comparison), NOT per instance:
/// two instances configured with the same webhook share one pacer and therefore one request budget. Normalizing both sides to the same canonical form keeps
/// config keys and request URIs in one key space — e.g. "http://host:port" and "http://host:port/" are the same endpoint, and a Uri created from either
/// renders identically (a raw-string key would split their pacing state). When several instances share a URL but configure different intervals, the smallest
/// (strictest) interval applies to that URL. URLs not present in the config — e.g. the operational dead-letter webhook from NEWSQUACK_DEADLETTER_WEBHOOK,
/// an env var rather than a schema key — still get paced: they receive an on-demand pacer with the default interval (same instance across calls), so EVERY
/// request through the "discord" client is paced (constraint 1). Boot validation already guarantees every configured URL is a well-formed absolute http(s) webhook.
/// </summary>
public sealed class WebhookPacerRegistry
{
    /// <summary>The v1.0 default minimum interval in seconds — applied when <c>MinSendIntervalSeconds</c> is omitted (default 3 s ⇒ at most 21 requests in any closed 60 s rolling window, sustained rate exactly 20/min, strictly below Discord's documented ~30 req/min per-webhook limit by construction).</summary>
    public const int DefaultMinSendIntervalSeconds = 3;

    private readonly ConcurrentDictionary<string, WebhookPacer> _byUrl;   // normalized Ordinal keys — the canonical webhook URL IS the key (constraint 3)

    public WebhookPacerRegistry(NewsQuackConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var intervals = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var instance in config.Instances ?? [])
        {
            // Boot validation guarantees every instance has a Discord section with a valid webhook URL; the guard only keeps this constructor total for
            // unvalidated configs (tests build configs directly).
            var discord = instance?.Discord;
            if (discord is null || string.IsNullOrWhiteSpace(discord.WebhookUrl))
                continue;

            var key = NormalizeKey(discord.WebhookUrl!);   // ! safe: the IsNullOrWhiteSpace check above guarantees non-null, non-empty
            var seconds = discord.MinSendIntervalSeconds ?? DefaultMinSendIntervalSeconds;   // omitted ⇒ default 3 s (T-051; T-048 constraint 2)

            // strictest wins on conflict (constraint 3): the smallest configured interval applies to a shared URL
            intervals[key] = intervals.TryGetValue(key, out var existing) ? Math.Min(existing, seconds) : seconds;
        }

        var byUrl = new ConcurrentDictionary<string, WebhookPacer>(StringComparer.Ordinal);
        foreach (var (key, seconds) in intervals)
            byUrl[key] = new WebhookPacer(TimeSpan.FromSeconds(seconds));

        _byUrl = byUrl;
    }

    /// <summary>The pacer for one webhook URL: the configured-interval pacer when the URL is in the validated config, else an on-demand default-interval
    /// pacer (the same instance across calls — unknown URLs share pacing state too).</summary>
    public WebhookPacer For(string url)
    {
        ArgumentNullException.ThrowIfNull(url);

        return _byUrl.GetOrAdd(NormalizeKey(url), key => new WebhookPacer(TimeSpan.FromSeconds(DefaultMinSendIntervalSeconds)));
    }

    /// <summary>Canonical key form: the <see cref="Uri.AbsoluteUri"/> of an absolute URI (lowercased scheme/host, resolved path — so a config string and the
    /// Uri a request actually carries always land on the same key), or the raw string when it is not parseable as an absolute URI (such a value can never be
    /// a request URI either, so falling back to it loses nothing).</summary>
    private static string NormalizeKey(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsoluteUri : url;
}
