using System.Net;

namespace Stowaway.Tests;

[Collection(ServiceHosts.Name)]
public sealed class AboutTests(ServiceFactory factory) : IClassFixture<ServiceFactory>
{
    [Fact]
    public async Task License_and_third_party_notices_ship_with_the_program()
    {
        var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var license = await client.GetAsync("/api/about/license", ct);
        Assert.Equal(HttpStatusCode.OK, license.StatusCode);
        var text = await license.Content.ReadAsStringAsync(ct);
        Assert.StartsWith("MIT License", text);
        Assert.Contains("Copyright (c) 2026 Thomas Kraft", text);

        var notices = await client.GetStringAsync("/api/about/notices", ct);
        Assert.Contains("third-party notices", notices);
        Assert.Contains("MailKit", notices);
        Assert.Contains("react-dom", notices);
    }
}
