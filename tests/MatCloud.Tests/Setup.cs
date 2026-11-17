using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using MatCloud.Contract;
using MatCloud.Core.Business;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace MatCloud.Tests;

/// <summary>Before any test: the modules of the host are installed (the model of the database is built from them), as at the start of the app.</summary>
internal static class TestSetup
{
    [ModuleInitializer]
    internal static void Install() => InstalledModules.Install();
}

/// <summary>The whole app in memory against a test database, and devices signed in over HTTP.</summary>
public static class TestApp
{
    public static WebApplicationFactory<Program> Create(TestDatabase test, string? dataDirectory = null, IDictionary<string, string?>? settings = null)
    {
        AppService.Forget();
        var data = dataDirectory ?? Directory.CreateTempSubdirectory("matcloud-test-").FullName;
        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");

            // the files of the modules (/_content/...), as in Development and in the published app
            b.UseStaticWebAssets();
            b.UseSetting("ConnectionStrings:Default", test.ConnectionString);
            b.UseSetting("DataDirectory", data);
            foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
            {
                b.UseSetting(key, value);
            }
        });
    }

    /// <summary>A client of a device of <paramref name="app"/> signed in with the password, with its bearer token.</summary>
    public static async Task<(HttpClient Client, DeviceLoginResponse Login)> DeviceAsync(
        WebApplicationFactory<Program> factory, string app, string device, string user = "anna", string password = "anna-password")
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync(CloudContract.DeviceLogin, new DeviceLoginRequest(user, password, device, "windows", "1.0.0", app), CloudContract.Json);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var answer = (await login.Content.ReadFromJsonAsync<DeviceLoginResponse>(CloudContract.Json))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", answer.Token);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ThixNote/1.0.1 (windows)");
        return (client, answer);
    }
}
