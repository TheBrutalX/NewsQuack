using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NewsQuack.App.Http;

/// <summary>
/// T-005: manual redirect following with per-hop anti-SSRF validation. .NET's automatic redirect following cannot validate
/// intermediate hops, so the inner handler runs with AllowAutoRedirect = false and this handler loops: send → if 3xx (301/302/303/307/308)
/// with a non-empty Location: resolve to an absolute URI, validate it, follow — up to <see cref="MaxRedirects"/> redirects total
/// (MaxAutomaticRedirections = 3 semantics: the final non-redirect response may be reached after at most 3 redirects); a 4th
/// redirect fails with an HttpRequestException naming "too many redirects". A failed hop also throws HttpRequestException naming
/// the offending location. The FIRST request of a loop is never validated by design (T-058): every initial URL in v1.0 is operator-trusted
/// and boot-validated (feeds/images via HttpUrlAttribute, webhook via DiscordWebhookUrlAttribute) — only redirect hops are. An https→http
/// downgrade on a hop logs a Warning but is never rejected: the security property is the private-network block; a feed may legitimately
/// redirect to an internal http mirror. Stateless per request except local loop variables — one instance can safely serve all three named clients.
/// </summary>
public sealed class SsrfSafeHttpHandler : DelegatingHandler
{
    /// <summary>The final non-redirect response may be reached after at most this many redirects; the next one fails.</summary>
    public const int MaxRedirects = 3;

    private static readonly HttpStatusCode[] RedirectStatuses = [HttpStatusCode.MovedPermanently, HttpStatusCode.Found, HttpStatusCode.SeeOther, HttpStatusCode.TemporaryRedirect, HttpStatusCode.PermanentRedirect];

    private readonly bool _blockPrivateNetworkRedirects;
    private readonly ILogger<SsrfSafeHttpHandler> _logger;

    public SsrfSafeHttpHandler(bool blockPrivateNetworkRedirects, HttpClientHandler? inner = null, ILogger<SsrfSafeHttpHandler>? logger = null)
        : base(inner ?? new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate })
    {
        _blockPrivateNetworkRedirects = blockPrivateNetworkRedirects;
        _logger = logger ?? NullLogger<SsrfSafeHttpHandler>.Instance;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var current = request;
        var redirectsFollowed = 0;

        while (true)
        {
            // Each hop uses the caller's token end-to-end — no local delays, no swallowing of cancellation.
            var response = await base.SendAsync(current, cancellationToken).ConfigureAwait(false);

            if (!RedirectStatuses.Contains(response.StatusCode))
                return response;   // final response — the caller owns it and disposes it

            // .NET does NOT resolve the Location header for us — relative values (the common case) stay relative, so we resolve them against
            // the current request URI exactly like HttpClient's own redirect logic does. A location that cannot be resolved at all is a failed hop.
            var rawLocation = response.Headers.Location;
            if (rawLocation is null)
            {
                response.Dispose();   // intermediate — we own it now (HttpResponseMessage has no async dispose in .NET)
                _logger.LogWarning("Redirect response {StatusCode} from {From} carried no usable Location header", (int)response.StatusCode, current.RequestUri);
                throw new HttpRequestException($"Redirect response {(int)response.StatusCode} from {current.RequestUri} carried no usable Location header.");
            }

            Uri location;
            try
            {
                // ! safe: HttpClient assigns an absolute RequestUri to every request before any handler runs (PrepareRequestMessage), so this never throws for a null base.
                location = rawLocation.IsAbsoluteUri ? rawLocation : new Uri(current.RequestUri!, rawLocation);
            }
            catch (UriFormatException ex)   // e.g. a relative Location that cannot be resolved against the request URI — same category as an empty one
            {
                response.Dispose();   // intermediate — we own it now (HttpResponseMessage has no async dispose in .NET)
                _logger.LogWarning(ex, "Redirect response {StatusCode} from {From} carried an unresolvable Location: {Location}", (int)response.StatusCode, current.RequestUri, rawLocation);
                throw new HttpRequestException($"Redirect response {(int)response.StatusCode} from {current.RequestUri} carried a Location that cannot be resolved against the request URI: '{rawLocation}'.", ex);
            }

            redirectsFollowed++;
            if (redirectsFollowed > MaxRedirects)
            {
                response.Dispose();   // intermediate — we own it now (HttpResponseMessage has no async dispose in .NET)
                _logger.LogWarning("Too many redirects: exceeded the limit of {MaxRedirects} hops at {Location}", MaxRedirects, location);
                throw new HttpRequestException($"Too many redirects: more than {MaxRedirects} hops while following a redirect to {location}.");
            }

            // T-058: an https→http downgrade is observable but never rejected (log only) — same-scheme hops log nothing.
            // ! safe: HttpClient assigns an absolute RequestUri before any handler runs (PrepareRequestMessage) — same invariant as above.
            if (current.RequestUri!.Scheme == Uri.UriSchemeHttps && location.Scheme == Uri.UriSchemeHttp)
                _logger.LogWarning("Redirect hop {Hop} downgrades TLS: {From} -> {To}", redirectsFollowed, current.RequestUri, location);

            var rejection = await ValidateLocationAsync(location, cancellationToken).ConfigureAwait(false);
            if (rejection is not null)
            {
                response.Dispose();   // intermediate — we own it now (HttpResponseMessage has no async dispose in .NET)
                _logger.LogWarning("Redirect hop {Hop} rejected ({Reason}): {From} -> {To}", redirectsFollowed, rejection, current.RequestUri, location);
                throw new HttpRequestException($"Redirect to {location} rejected: {rejection}.");
            }

            _logger.LogDebug("Following redirect hop {Hop}: {From} -> {To}", redirectsFollowed, current.RequestUri, location);

            response.Dispose();   // intermediate — we own it now (HttpResponseMessage has no async dispose in .NET)

            // 303 always becomes GET (body dropped); the other codes keep the method and body. Request-level headers are carried over
            // so a hop never silently loses what the caller set on the original request.
            var method = response.StatusCode == HttpStatusCode.SeeOther ? HttpMethod.Get : current.Method;
            var next = new HttpRequestMessage(method, location) { Content = method == current.Method ? current.Content : null };
            foreach (var header in current.Headers)
                next.Headers.TryAddWithoutValidation(header.Key, header.Value);

            current = next;
        }
    }

    /// <summary>Per-hop validation of REDIRECT HOPS only — the first request is intentionally not validated (operator-trusted URL, boot-validated;
    /// see the class docs). Scheme must be http/https and the host non-empty. When the anti-SSRF switch is on, the target must not be
    /// unspecified, loopback, link-local, or private — IP literals are checked directly, DNS names are resolved (all addresses) and rejected if ANY resolved
    /// address is in a blocked range. Returns null when the hop is allowed, otherwise the human-readable reason (used in log + exception).</summary>
    private async Task<string?> ValidateLocationAsync(Uri location, CancellationToken cancellationToken)
    {
        if (location.Scheme is not ("http" or "https"))
            return $"scheme '{location.Scheme}' must be http or https";

        var host = location.Host;
        if (host.Length == 0)
            return "empty host";

        if (!_blockPrivateNetworkRedirects)
            return null;

        var literal = TryParseIpLiteral(location);
        if (literal is not null)
            return SsrfValidation.IsBlockedAddress(literal) ? $"{literal} is in a blocked private-network range" : null;

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException ex)   // fail closed: an unresolvable name must not open the local network — the send would fail anyway, and a hostile resolver must not get a free pass
        {
            _logger.LogDebug(ex, "DNS resolution failed for host {Host}: {Message}", host, ex.Message);   // full stack at Debug; the rejection Warning in SendAsync names the hop
            return $"DNS resolution failed for host '{host}' ({ex.Message})";
        }

        foreach (var address in addresses)   // any-address rule: one blocked address among public ones rejects the hop
        {
            if (SsrfValidation.IsBlockedAddress(address))
                return $"resolves to blocked private-network address {address}";
        }

        return null;
    }

    /// <summary>The host as a direct IP literal when it is one (no DNS lookup), else null. Strips the brackets .NET keeps on IPv6 Uri.Host.</summary>
    private static IPAddress? TryParseIpLiteral(Uri location)
    {
        var host = location.Host;
        if (host.Length >= 2 && host[0] == '[' && host[^1] == ']')
            host = host[1..^1];

        return IPAddress.TryParse(host, out var address) ? address : null;
    }
}
