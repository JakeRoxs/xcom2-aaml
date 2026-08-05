using AAML.Application.Common;
using AAML.Application.Mods.Metadata;
using AAML.Application.Ports;
using AAML.Application.Settings;
using AAML.Domain.Games;
using AAML.Domain.Mods;
using FluentAssertions;

namespace AAML.Application.Tests;

[TestClass]
public sealed class ModMetadataServiceTests
{
    [TestMethod]
    public async Task TagColor_IsValidatedAndPreservesTaxonomyIdentity()
    {
        var repository = new RecordingRepository();
        var service = new ModMetadataService(repository);
        var tag = new Tag(new TagId("color"), "Color");
        var settings = Settings([], [], [tag]);

        var saved = await service.SetTagColorAsync(settings, tag.Id, "#12ab34", TestContext.CancellationToken);
        var invalid = await service.SetTagColorAsync(settings, tag.Id, "red", TestContext.CancellationToken);

        saved.Value!.Tags.Single().Should().Be(tag with { Color = "#12AB34" });
        invalid.Error!.Code.Should().Be("metadata.color_invalid");
    }

    [TestMethod]
    public async Task DescriptorTaxonomyAdoption_IsDeterministicAtomicAndPreservesExistingTags()
    {
        var repository = new RecordingRepository(); var service = new ModMetadataService(repository); var mod = Key("Adopt");
        var existing = new Tag(new TagId("existing"), "Existing");
        var settings = Settings([new ModUserIntent(mod, true, false, 2, null, null, new HashSet<TagId> { existing.Id }, null, new HashSet<WorkshopId>())], [], [existing]);

        var result = await service.AdoptDescriptorTaxonomyAsync(settings, mod, "Gameplay", ["Strategy", "strategy"], TestContext.CancellationToken);

        result.Value!.Categories.Should().ContainSingle(category => category.Name == "Gameplay");
        result.Value.Tags.Should().HaveCount(2);
        result.Value.ModIntents.Single().Tags.Should().HaveCount(2).And.Contain(existing.Id);
        repository.Saved.Should().Be(result.Value);
    }
    [TestMethod]
    public async Task SaveMetadata_PreservesActivationOrderAndIgnoredDependencies()
    {
        var key = Key("One");
        var category = new Category(new CategoryId("gameplay"), "Gameplay", 0);
        var tag = new Tag(new TagId("stable"), "Stable");
        var original = new ModUserIntent(key, true, false, 7, null, null, new HashSet<TagId>(), null, new HashSet<WorkshopId> { new(123) });
        var repository = new RecordingRepository();
        var service = new ModMetadataService(repository);

        var result = await service.SaveAsync(Settings([original], [category], [tag]), key,
            new ModMetadata("Custom Name", "Detailed note", true, category.Id, new HashSet<TagId> { tag.Id }), TestContext.CancellationToken);

        result.Value!.ModIntents.Single().Should().BeEquivalentTo(original with
        {
            ManualName = "Custom Name", Note = "Detailed note", IsHidden = true, Category = category.Id, Tags = new HashSet<TagId> { tag.Id }
        });
        repository.Saved.Should().Be(result.Value);
    }

    [TestMethod]
    public async Task BulkCategoryAndTag_PreservePerModUnrelatedFields()
    {
        var first = new ModUserIntent(Key("One"), true, false, 1, "One", null, new HashSet<TagId>(), "First", new HashSet<WorkshopId>());
        var second = new ModUserIntent(Key("Two"), false, true, null, "Two", null, new HashSet<TagId>(), "Second", new HashSet<WorkshopId>());
        var category = new Category(new CategoryId("ui"), "UI", 0);
        var tag = new Tag(new TagId("tested"), "Tested");
        var repository = new RecordingRepository();
        var service = new ModMetadataService(repository);

        var categorized = await service.AssignCategoryAsync(Settings([first, second], [category], [tag]), [first.Mod, second.Mod], category.Id, TestContext.CancellationToken);
        var tagged = await service.AddTagsAsync(categorized.Value!, [first.Mod, second.Mod], new HashSet<TagId> { tag.Id }, TestContext.CancellationToken);

        tagged.Value!.ModIntents.Should().OnlyContain(intent => intent.Category == category.Id && intent.Tags.Contains(tag.Id));
        tagged.Value.ModIntents.Single(intent => intent.Mod == first.Mod).Should().BeEquivalentTo(first with { Category = category.Id, Tags = new HashSet<TagId> { tag.Id } });
        tagged.Value.ModIntents.Single(intent => intent.Mod == second.Mod).Should().BeEquivalentTo(second with { Category = category.Id, Tags = new HashSet<TagId> { tag.Id } });
        var removed = await service.RemoveTagsAsync(tagged.Value, [first.Mod, second.Mod], new HashSet<TagId> { tag.Id }, TestContext.CancellationToken);
        removed.Value!.ModIntents.Should().OnlyContain(intent => intent.Tags.Count == 0 && intent.Category == category.Id);
    }

    [TestMethod]
    public async Task TaxonomyCreateRenameReorderDelete_CleansReferencesAndNormalizesOrder()
    {
        var repository = new RecordingRepository();
        var service = new ModMetadataService(repository);
        var createdA = await service.CreateCategoryAsync(Settings([], [], []), "Gameplay", TestContext.CancellationToken);
        var createdB = await service.CreateCategoryAsync(createdA.Value!, "UI", TestContext.CancellationToken);
        var categoryA = createdB.Value!.Categories.Single(category => category.Name == "Gameplay");
        var reordered = await service.ReorderCategoryAsync(createdB.Value, categoryA.Id, 1, TestContext.CancellationToken);
        var renamed = await service.RenameCategoryAsync(reordered.Value!, categoryA.Id, "Mechanics", TestContext.CancellationToken);
        var tagged = await service.CreateTagAsync(renamed.Value!, "Stable", TestContext.CancellationToken);
        var tag = tagged.Value!.Tags.Single();
        var key = Key("One");
        var edited = await service.SaveAsync(tagged.Value, key, new ModMetadata(null, null, false, categoryA.Id, new HashSet<TagId> { tag.Id }), TestContext.CancellationToken);
        var categoryDeleted = await service.DeleteCategoryAsync(edited.Value!, categoryA.Id, TestContext.CancellationToken);
        var tagDeleted = await service.DeleteTagAsync(categoryDeleted.Value!, tag.Id, TestContext.CancellationToken);

        tagDeleted.Value!.Categories.Should().ContainSingle(category => category.Name == "UI" && category.Order == 0);
        tagDeleted.Value.Tags.Should().BeEmpty();
        tagDeleted.Value.ModIntents.Should().BeEmpty();
    }

    [TestMethod]
    public async Task DuplicateTaxonomyName_FailsWithoutSaving()
    {
        var repository = new RecordingRepository();
        var service = new ModMetadataService(repository);
        var settings = Settings([], [new Category(new CategoryId("one"), "Gameplay", 0)], []);

        var result = await service.CreateCategoryAsync(settings, " gameplay ", TestContext.CancellationToken);

        result.Error!.Code.Should().Be("metadata.name_conflict");
        repository.Saved.Should().BeNull();
    }

    public TestContext TestContext { get; set; }
    private static ModKey Key(string value) => new(ModSource.Manual, $"C:\\Mods\\{value}");
    private static ApplicationSettings Settings(IReadOnlyList<ModUserIntent> intents, IReadOnlyList<Category> categories, IReadOnlyList<Tag> tags) =>
        new(ApplicationSettingsDefaults.CurrentSchemaVersion, GameVariant.XCom2, null, [], [], intents, categories, tags);

    private sealed class RecordingRepository : ISettingsRepository
    {
        public ApplicationSettings? Saved { get; private set; }
        public Task<Result<ApplicationSettings>> LoadAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Result> SaveAsync(ApplicationSettings settings, CancellationToken cancellationToken) { Saved = settings; return Task.FromResult(Result.Success()); }
    }
}
