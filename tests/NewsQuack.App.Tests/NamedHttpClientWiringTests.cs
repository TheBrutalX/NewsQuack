using NewsQuack.App.Configuration;
using NewsQuack.App.Discord;
using NewsQuack.App.Http;
using NewsQuack.Core;
using Microsoft.Extensions.DependencyInjection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace NewsQuack.App.Tests;

/// <summary>T-005: the named-client wiring itself — per-client timeouts read from the (validated) config, and proof that the shared handler is
/// transparent for T-020's no-redirect scenarios (its WireMock setups must keep passing unchanged).</summary>
public class NamedHttpClientWiringTests
{
    [Fact]
    public void HttpClientFactory_ConfiguredClients_HaveConfiguredTimeouts()
    {
        using var provider = CreateProvider(new HttpSectionConfig { FeedTimeoutSeconds = 42, DiscordTimeoutSeconds = 7, ImageTimeoutSeconds = 9 });

        var factory = provider.GetRequiredService<IHttpClientFactory>();

        Assert.Equal(TimeSpan.FromSeconds(42), factory.CreateClient("feed").Timeout);
        Assert.Equal(TimeSpan.FromSeconds(7), factory.CreateClient("discord").Timeout);
        Assert.Equal(TimeSpan.FromSeconds(9), factory.CreateClient("image").Timeout);
    }

    [Fact]
    public void HttpClientFactory_OmittedHttpSection_UsesSchemaDefaults()
    {
        using var provider = CreateProvider(null);   // null section → schema defaults (30/15/15 s) apply through the wiring

        var factory = provider.GetRequiredService<IHttpClientFactory>();

        Assert.Equal(TimeSpan.FromSeconds(30), factory.CreateClient("feed").Timeout);
        Assert.Equal(TimeSpan.FromSeconds(15), factory.CreateClient("discord").Timeout);
        Assert.Equal(TimeSpan.FromSeconds(15), factory.CreateClient("image").Timeout);
    }

    [Fact]
    public async Task NoRedirects_ExistingDiscordBehavior_UnchangedByHandler()
    {
        // Reuses T-020's scenario shape: a target pointed at WireMock on a dynamic port, but with the REAL production wiring in front —
        // the shared SsrfSafeHttpHandler sits in the "discord" client's pipeline for this one. No redirects → single send, same mapping.
        var server = WireMockServer.Start();

        try
        {
            server.Given(Request.Create()).RespondWith(Response.Create().WithStatusCode(204));

            using var provider = CreateProvider(new HttpSectionConfig());   // default 15 s timeout — ample for an instant local response
            var target = new DiscordNotificationTarget(provider.GetRequiredService<IHttpClientFactory>(), new DiscordTargetConfig { WebhookUrl = server.Url });

            var result = await target.SendAsync(MakePost());

            Assert.Equal(TargetOutcome.Delivered, result.Outcome);   // 204 → Delivered exactly as T-020 maps it
            Assert.Single(server.LogEntries);                        // exactly one POST — the handler is transparent without redirects
        }
        finally
        {
            server.Dispose();
        }
    }

    private static ServiceProvider CreateProvider(HttpSectionConfig? http)
    {
        var services = new ServiceCollection();
        services.AddNewsquackHttpClients(http);   // the exact wiring Program.cs uses on both host paths
        return services.BuildServiceProvider();
    }

    private static Post MakePost() => new()
    {
        Id = Guid.NewGuid().ToString(),
        ItemId = "item-1",
        Title = "Hello",
        Description = null,
        Link = "https://example.com/item",
        Author = null,
        PublishedAt = new DateTimeOffset(2026, 10, 23, 14, 5, 9, TimeSpan.Zero),   // UTC — same fixed instant as DiscordTargetSendTests
        ImageUrl = null,
        SourceUrl = "https://example.com/feed.xml",
    };
}
