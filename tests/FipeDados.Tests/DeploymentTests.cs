using System.Reflection;
using FipeDados.Web.Deployment;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;

namespace FipeDados.Tests;

public class DeploymentTests
{
    [Fact]
    public async Task Version_ReturnsCompiledRevision_WithoutCaching_OrAuthentication()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();
        app.MapDeploymentEndpoints();
        await app.StartAsync();

        using var response = await app.GetTestClient().GetAsync("/health/version?revision=not-the-version");
        response.EnsureSuccessStatusCode();
        var informationalVersion = typeof(DeploymentEndpoints).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Assert.Equal(informationalVersion.Split('+', 2)[1], await response.Content.ReadAsStringAsync());
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.True(response.Headers.CacheControl.NoCache);
        Assert.Equal("noindex, nofollow", response.Headers.GetValues("X-Robots-Tag").Single());
    }
}
