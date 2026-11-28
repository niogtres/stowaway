using System.Net;
using System.Net.Http.Json;
using Stowaway.Service.Hosting;

namespace Stowaway.Tests.Inventory;

[Collection(ServiceHosts.Name)]
public sealed class IncludeFolderApiTests(ServiceFactory factory) : IClassFixture<ServiceFactory>
{
    [Fact]
    public async Task Only_folders_a_container_maps_in_can_be_included()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(RequestOriginGuard.HeaderName, "1");

        // Not mapped into any container: refused, so the API can't be used to back up arbitrary host folders.
        var response = await client.PostAsJsonAsync("/api/inventory/folders", new { path = @"C:\Users", include = true }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/inventory/folders", new { path = " ", include = true }, ct)).StatusCode);

        // Stopping is always allowed (the container may be gone).
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/inventory/folders", new { path = @"C:\gone", include = false }, ct)).StatusCode);
    }
}
