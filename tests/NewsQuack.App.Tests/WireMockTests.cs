using Xunit;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace NewsQuack.App.Tests;

/// <summary>Proves the WireMock.Net wiring works end-to-end in this project as well (it will host heavier integration tests later).</summary>
public class WireMockTests
{
    [Fact]
    public async Task WireMock_SelfHosted_RoundTrip_ReturnsExpectedBody()
    {
        const string expected = "<rss version=\"2.0\"><channel><title>known body</title></channel></rss>";

        using var server = WireMockServer.Start();   // no port argument -> dynamic free port, no fixed binding (CI-safe)
        server.Given(Request.Create().WithPath("/feed"))
              .RespondWith(Response.Create().WithBody(expected).WithStatusCode(200));

        using var http = new HttpClient();
        var body = await http.GetStringAsync(server.Url + "/feed");

        Assert.Equal(expected, body);
    }
}
