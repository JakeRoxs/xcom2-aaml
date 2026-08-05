using System.Net;
using AAML.Infrastructure.Common.Compatibility.GitHub;
using FluentAssertions;

namespace AAML.Infrastructure.Common.CharacterizationTests.GitHub;

[TestClass]
public sealed class GitHubHttpIsolationTests
{
    [TestMethod]
    public async Task FixtureResponse_IsParsedWithoutNetworkAccess()
    {
        var json = CompatibilityFixture.Read("http", "github-release.json");
        using var client = new HttpClient(new FixtureHandler(json));

        var response = await client.GetStringAsync("https://api.example.invalid/releases/latest", TestContext.CancellationToken);
        var result = LegacyGitHubReleaseCodec.Parse(response);

        result.TagName.Should().Be("v9.9.9");
    }

    public TestContext TestContext { get; set; }

    private sealed class FixtureHandler(string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content),
                RequestMessage = request
            });
    }
}
