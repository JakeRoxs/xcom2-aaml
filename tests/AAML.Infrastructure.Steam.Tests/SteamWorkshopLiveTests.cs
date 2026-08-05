using FluentAssertions;

namespace AAML.Infrastructure.Steam.Tests;

[TestClass]
[TestCategory("SteamLive")]
public sealed class SteamWorkshopLiveTests
{
    [TestMethod]
    public async Task ReadOnlySubscribedState_DoesNotRequestDownloadsOrAlterSubscriptions()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("AAML_RUN_STEAM_LIVE"), "1", StringComparison.Ordinal))
            Assert.Inconclusive("Set AAML_RUN_STEAM_LIVE=1 to run read-only Steam Workshop validation.");

        await using var client = SteamWorkshopClient.Create();
        var before = await client.Workshop.GetSubscribedItemsAsync(TestContext.CancellationToken);
        before.IsSuccess.Should().BeTrue();
        foreach (var id in before.Value!.Take(10))
            (await client.Workshop.GetLocalStateAsync(id, TestContext.CancellationToken)).IsSuccess.Should().BeTrue();
        var after = await client.Workshop.GetSubscribedItemsAsync(TestContext.CancellationToken);
        after.Value.Should().Equal(before.Value);
    }

    [TestMethod]
    public async Task ExplicitMutation_RestoresOriginalSubscriptionState()
    {
        var rawId = 0UL;
        if (!string.Equals(Environment.GetEnvironmentVariable("AAML_RUN_STEAM_MUTATION_LIVE"), "1", StringComparison.Ordinal) || !ulong.TryParse(Environment.GetEnvironmentVariable("AAML_STEAM_MUTATION_WORKSHOP_ID"), out rawId))
            Assert.Inconclusive("Set AAML_RUN_STEAM_MUTATION_LIVE=1 and AAML_STEAM_MUTATION_WORKSHOP_ID to run controlled subscription validation.");
        await using var client = SteamWorkshopClient.Create(); var id = new AAML.Domain.Mods.WorkshopId(rawId);
        var before = await client.Workshop.GetSubscribedItemsAsync(TestContext.CancellationToken); before.IsSuccess.Should().BeTrue(); var wasSubscribed = before.Value!.Contains(id);
        try { (wasSubscribed ? await client.Workshop.UnsubscribeAsync(id, TestContext.CancellationToken) : await client.Workshop.SubscribeAsync(id, TestContext.CancellationToken)).IsSuccess.Should().BeTrue(); }
        finally { (wasSubscribed ? await client.Workshop.SubscribeAsync(id, TestContext.CancellationToken) : await client.Workshop.UnsubscribeAsync(id, TestContext.CancellationToken)).IsSuccess.Should().BeTrue(); }
        (await client.Workshop.GetSubscribedItemsAsync(TestContext.CancellationToken)).Value.Should().BeEquivalentTo(before.Value);
    }

    public TestContext TestContext { get; set; }
}
