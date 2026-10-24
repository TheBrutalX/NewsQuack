namespace NewsQuack.App.Discord;

/// <summary>
/// T-048: the pacing <see cref="DelegatingHandler"/> on the named "discord" HttpClient (task constraint 4) — paces EVERY request to a webhook URL, not just
/// posts: both POSTs of the target's one-shot channel-type fallback swap traverse this handler, so requests are counted, never posts. The send worker stays
/// target-agnostic (AGENTS.md): it knows nothing about pacing; the composition happens here, in the Discord HTTP path, where every webhook request already
/// funnels through. A null registry — bare test hosts that register their own "discord" client without <c>AddWebhookPacing</c> — makes the handler a
/// transparent pass-through; production always registers the registry, so every production request is paced. Cancellation during the pacing wait propagates
/// as OCE promptly (constraint 6): it must never block the shutdown drain.
/// </summary>
public sealed class WebhookPacingHandler : DelegatingHandler
{
    private readonly WebhookPacerRegistry? _registry;   // null ⇒ pass-through (no registry registered — bare test hosts)

    public WebhookPacingHandler(WebhookPacerRegistry? registry) => _registry = registry;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_registry is { } registry && request.RequestUri is { } uri)
            await registry.For(uri.AbsoluteUri).WaitUntilAllowedAsync(cancellationToken);   // pace BEFORE the request starts — first request to a URL is never delayed; OCE propagates (constraint 6)

        return await base.SendAsync(request, cancellationToken);
    }
}
