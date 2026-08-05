using AAML.Application.Common;
using AAML.Application.Ports;
using AAML.Application.Settings;
using AAML.Domain.Mods;

namespace AAML.Application.Mods.Metadata;

public sealed record ModMetadata(string? ManualName, string? Note, bool IsHidden, CategoryId? Category, IReadOnlySet<TagId> Tags);

public interface IModMetadataService
{
    Task<Result<ApplicationSettings>> SaveAsync(ApplicationSettings settings, ModKey mod, ModMetadata metadata, CancellationToken cancellationToken);
    Task<Result<ApplicationSettings>> AssignCategoryAsync(ApplicationSettings settings, IReadOnlyCollection<ModKey> mods, CategoryId? category, CancellationToken cancellationToken);
    Task<Result<ApplicationSettings>> AddTagsAsync(ApplicationSettings settings, IReadOnlyCollection<ModKey> mods, IReadOnlySet<TagId> tags, CancellationToken cancellationToken);
    Task<Result<ApplicationSettings>> RemoveTagsAsync(ApplicationSettings settings, IReadOnlyCollection<ModKey> mods, IReadOnlySet<TagId> tags, CancellationToken cancellationToken);
    Task<Result<ApplicationSettings>> SetHiddenAsync(ApplicationSettings settings, IReadOnlyCollection<ModKey> mods, bool hidden, CancellationToken cancellationToken);
    Task<Result<ApplicationSettings>> CreateCategoryAsync(ApplicationSettings settings, string name, CancellationToken cancellationToken);
    Task<Result<ApplicationSettings>> RenameCategoryAsync(ApplicationSettings settings, CategoryId id, string name, CancellationToken cancellationToken);
    Task<Result<ApplicationSettings>> ReorderCategoryAsync(ApplicationSettings settings, CategoryId id, int order, CancellationToken cancellationToken);
    Task<Result<ApplicationSettings>> DeleteCategoryAsync(ApplicationSettings settings, CategoryId id, CancellationToken cancellationToken);
    Task<Result<ApplicationSettings>> CreateTagAsync(ApplicationSettings settings, string name, CancellationToken cancellationToken);
    Task<Result<ApplicationSettings>> RenameTagAsync(ApplicationSettings settings, TagId id, string name, CancellationToken cancellationToken);
    Task<Result<ApplicationSettings>> DeleteTagAsync(ApplicationSettings settings, TagId id, CancellationToken cancellationToken);
    Task<Result<ApplicationSettings>> SetTagColorAsync(ApplicationSettings settings, TagId id, string? color, CancellationToken cancellationToken);
    Task<Result<ApplicationSettings>> AdoptDescriptorTaxonomyAsync(ApplicationSettings settings, ModKey mod, string? categoryName, IReadOnlyList<string> tagNames, CancellationToken cancellationToken);
}

/// <summary>Owns user-authored mod metadata and taxonomy without changing activation or dependency decisions.</summary>
public sealed class ModMetadataService(ISettingsRepository repository) : IModMetadataService
{
    public Task<Result<ApplicationSettings>> SaveAsync(ApplicationSettings settings, ModKey mod, ModMetadata metadata, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var valid = ValidateReferences(settings, metadata.Category, metadata.Tags);
        if (!valid.IsSuccess) return Task.FromResult(Result<ApplicationSettings>.Failure(valid.Error!));
        var intents = settings.ModIntents.ToDictionary(intent => intent.Mod);
        var intent = GetOrCreate(intents, mod) with
        {
            ManualName = Normalize(metadata.ManualName),
            Note = Normalize(metadata.Note),
            IsHidden = metadata.IsHidden,
            Category = metadata.Category,
            Tags = metadata.Tags.ToHashSet()
        };
        Store(intents, intent);
        return PersistAsync(settings with { ModIntents = Order(intents) }, cancellationToken);
    }

    public Task<Result<ApplicationSettings>> AssignCategoryAsync(ApplicationSettings settings, IReadOnlyCollection<ModKey> mods, CategoryId? category, CancellationToken cancellationToken)
    {
        if (category is { } id && settings.Categories.All(item => item.Id != id)) return Task.FromResult(Failure("metadata.category_missing", "The selected category does not exist."));
        var intents = settings.ModIntents.ToDictionary(intent => intent.Mod);
        foreach (var mod in mods.Distinct()) Store(intents, GetOrCreate(intents, mod) with { Category = category });
        return PersistAsync(settings with { ModIntents = Order(intents) }, cancellationToken);
    }

    public Task<Result<ApplicationSettings>> AddTagsAsync(ApplicationSettings settings, IReadOnlyCollection<ModKey> mods, IReadOnlySet<TagId> tags, CancellationToken cancellationToken)
    {
        var missing = tags.FirstOrDefault(id => settings.Tags.All(tag => tag.Id != id));
        if (missing != default) return Task.FromResult(Failure("metadata.tag_missing", $"Tag does not exist: {missing.Value}."));
        var intents = settings.ModIntents.ToDictionary(intent => intent.Mod);
        foreach (var mod in mods.Distinct())
        {
            var intent = GetOrCreate(intents, mod);
            Store(intents, intent with { Tags = intent.Tags.Concat(tags).ToHashSet() });
        }
        return PersistAsync(settings with { ModIntents = Order(intents) }, cancellationToken);
    }

    public Task<Result<ApplicationSettings>> RemoveTagsAsync(ApplicationSettings settings, IReadOnlyCollection<ModKey> mods, IReadOnlySet<TagId> tags, CancellationToken cancellationToken)
    {
        var intents = settings.ModIntents.ToDictionary(intent => intent.Mod);
        foreach (var mod in mods.Distinct())
            if (intents.TryGetValue(mod, out var intent)) Store(intents, intent with { Tags = intent.Tags.Except(tags).ToHashSet() });
        return PersistAsync(settings with { ModIntents = Order(intents) }, cancellationToken);
    }

    public Task<Result<ApplicationSettings>> SetHiddenAsync(ApplicationSettings settings, IReadOnlyCollection<ModKey> mods, bool hidden, CancellationToken cancellationToken)
    {
        var intents = settings.ModIntents.ToDictionary(intent => intent.Mod);
        foreach (var mod in mods.Distinct())
        {
            var intent = intents.GetValueOrDefault(mod) ?? new ModUserIntent(mod, false, false, null, null, null, new HashSet<TagId>(), null, new HashSet<WorkshopId>());
            Store(intents, intent with { IsHidden = hidden });
        }
        return PersistAsync(settings with { ModIntents = Order(intents) }, cancellationToken);
    }

    public Task<Result<ApplicationSettings>> CreateCategoryAsync(ApplicationSettings settings, string name, CancellationToken cancellationToken)
    {
        var valid = ValidateName(name, settings.Categories.Select(category => category.Name));
        if (!valid.IsSuccess) return Task.FromResult(Result<ApplicationSettings>.Failure(valid.Error!));
        var category = new Category(new CategoryId("category-" + Guid.NewGuid().ToString("N")), name.Trim(), settings.Categories.Count);
        return PersistAsync(settings with { Categories = settings.Categories.Append(category).ToArray() }, cancellationToken);
    }

    public Task<Result<ApplicationSettings>> RenameCategoryAsync(ApplicationSettings settings, CategoryId id, string name, CancellationToken cancellationToken)
    {
        if (settings.Categories.All(category => category.Id != id)) return Task.FromResult(Failure("metadata.category_missing", "The category does not exist."));
        var valid = ValidateName(name, settings.Categories.Where(category => category.Id != id).Select(category => category.Name));
        if (!valid.IsSuccess) return Task.FromResult(Result<ApplicationSettings>.Failure(valid.Error!));
        return PersistAsync(settings with { Categories = settings.Categories.Select(category => category.Id == id ? category with { Name = name.Trim() } : category).ToArray() }, cancellationToken);
    }

    public Task<Result<ApplicationSettings>> ReorderCategoryAsync(ApplicationSettings settings, CategoryId id, int order, CancellationToken cancellationToken)
    {
        if (order < 0 || order >= settings.Categories.Count) return Task.FromResult(Failure("metadata.category_order_invalid", "Category order is outside the valid range."));
        var selected = settings.Categories.SingleOrDefault(category => category.Id == id);
        if (selected is null) return Task.FromResult(Failure("metadata.category_missing", "The category does not exist."));
        var categories = settings.Categories.Where(category => category.Id != id).OrderBy(category => category.Order).ToList();
        categories.Insert(order, selected);
        return PersistAsync(settings with { Categories = categories.Select((category, index) => category with { Order = index }).ToArray() }, cancellationToken);
    }

    public Task<Result<ApplicationSettings>> DeleteCategoryAsync(ApplicationSettings settings, CategoryId id, CancellationToken cancellationToken)
    {
        if (settings.Categories.All(category => category.Id != id)) return Task.FromResult(Failure("metadata.category_missing", "The category does not exist."));
        var intents = settings.ModIntents.ToDictionary(intent => intent.Mod);
        foreach (var intent in intents.Values.Where(intent => intent.Category == id).ToArray()) Store(intents, intent with { Category = null });
        var categories = settings.Categories.Where(category => category.Id != id).OrderBy(category => category.Order).Select((category, index) => category with { Order = index }).ToArray();
        return PersistAsync(settings with { Categories = categories, ModIntents = Order(intents) }, cancellationToken);
    }

    public Task<Result<ApplicationSettings>> CreateTagAsync(ApplicationSettings settings, string name, CancellationToken cancellationToken)
    {
        var valid = ValidateName(name, settings.Tags.Select(tag => tag.Name));
        if (!valid.IsSuccess) return Task.FromResult(Result<ApplicationSettings>.Failure(valid.Error!));
        var tag = new Tag(new TagId("tag-" + Guid.NewGuid().ToString("N")), name.Trim());
        return PersistAsync(settings with { Tags = settings.Tags.Append(tag).ToArray() }, cancellationToken);
    }

    public Task<Result<ApplicationSettings>> RenameTagAsync(ApplicationSettings settings, TagId id, string name, CancellationToken cancellationToken)
    {
        if (settings.Tags.All(tag => tag.Id != id)) return Task.FromResult(Failure("metadata.tag_missing", "The tag does not exist."));
        var valid = ValidateName(name, settings.Tags.Where(tag => tag.Id != id).Select(tag => tag.Name));
        if (!valid.IsSuccess) return Task.FromResult(Result<ApplicationSettings>.Failure(valid.Error!));
        return PersistAsync(settings with { Tags = settings.Tags.Select(tag => tag.Id == id ? tag with { Name = name.Trim() } : tag).ToArray() }, cancellationToken);
    }

    public Task<Result<ApplicationSettings>> DeleteTagAsync(ApplicationSettings settings, TagId id, CancellationToken cancellationToken)
    {
        if (settings.Tags.All(tag => tag.Id != id)) return Task.FromResult(Failure("metadata.tag_missing", "The tag does not exist."));
        var intents = settings.ModIntents.ToDictionary(intent => intent.Mod);
        foreach (var intent in intents.Values.Where(intent => intent.Tags.Contains(id)).ToArray()) Store(intents, intent with { Tags = intent.Tags.Where(tag => tag != id).ToHashSet() });
        return PersistAsync(settings with { Tags = settings.Tags.Where(tag => tag.Id != id).ToArray(), ModIntents = Order(intents) }, cancellationToken);
    }

    public Task<Result<ApplicationSettings>> SetTagColorAsync(ApplicationSettings settings, TagId id, string? color, CancellationToken cancellationToken)
    {
        var normalized = string.IsNullOrWhiteSpace(color) ? null : color.Trim().ToUpperInvariant();
        if (normalized is not null && !System.Text.RegularExpressions.Regex.IsMatch(normalized, "^#[0-9A-F]{6}([0-9A-F]{2})?$")) return Task.FromResult(Result<ApplicationSettings>.Failure(new Error("metadata.color_invalid", "Tag color must be #RRGGBB or #RRGGBBAA.", ErrorKind.Validation)));
        if (!settings.Tags.Any(tag => tag.Id == id)) return Task.FromResult(Result<ApplicationSettings>.Failure(new Error("metadata.tag_missing", "Tag was not found.", ErrorKind.NotFound)));
        return PersistAsync(settings with { Tags = settings.Tags.Select(tag => tag.Id == id ? tag with { Color = normalized } : tag).ToArray() }, cancellationToken);
    }

    public Task<Result<ApplicationSettings>> AdoptDescriptorTaxonomyAsync(ApplicationSettings settings, ModKey mod, string? categoryName, IReadOnlyList<string> tagNames, CancellationToken cancellationToken)
    {
        var categories = settings.Categories.ToList(); var tags = settings.Tags.ToList();
        CategoryId? categoryId = null;
        if (!string.IsNullOrWhiteSpace(categoryName))
        {
            var category = categories.FirstOrDefault(item => item.Name.Equals(categoryName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (category is null) { category = new Category(new CategoryId("descriptor-category-" + Stable(categoryName)), categoryName.Trim(), categories.Count); categories.Add(category); }
            categoryId = category.Id;
        }
        var tagIds = new HashSet<TagId>();
        foreach (var name in tagNames.Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var tag = tags.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (tag is null) { tag = new Tag(new TagId("descriptor-tag-" + Stable(name)), name); tags.Add(tag); }
            tagIds.Add(tag.Id);
        }
        var intents = settings.ModIntents.ToDictionary(intent => intent.Mod);
        var intent = intents.GetValueOrDefault(mod) ?? new ModUserIntent(mod, false, false, null, null, null, new HashSet<TagId>(), null, new HashSet<WorkshopId>());
        Store(intents, intent with { Category = categoryId ?? intent.Category, Tags = intent.Tags.Concat(tagIds).ToHashSet() });
        return PersistAsync(settings with { Categories = categories, Tags = tags, ModIntents = Order(intents) }, cancellationToken);
    }

    private static string Stable(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value.Trim().ToUpperInvariant())))[..16].ToLowerInvariant();

    private async Task<Result<ApplicationSettings>> PersistAsync(ApplicationSettings settings, CancellationToken cancellationToken)
    {
        var saved = await repository.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        return saved.IsSuccess ? Result<ApplicationSettings>.Success(settings) : Result<ApplicationSettings>.Failure(saved.Error!);
    }

    private static Result ValidateReferences(ApplicationSettings settings, CategoryId? category, IReadOnlySet<TagId> tags)
    {
        if (category is { } categoryId && settings.Categories.All(item => item.Id != categoryId)) return Result.Failure(new Error("metadata.category_missing", "The selected category does not exist.", ErrorKind.NotFound));
        var missing = tags.FirstOrDefault(id => settings.Tags.All(tag => tag.Id != id));
        return missing == default ? Result.Success() : Result.Failure(new Error("metadata.tag_missing", $"Tag does not exist: {missing.Value}.", ErrorKind.NotFound));
    }

    private static Result ValidateName(string name, IEnumerable<string> existing)
    {
        if (string.IsNullOrWhiteSpace(name)) return Result.Failure(new Error("metadata.name_required", "A non-empty name is required.", ErrorKind.Validation));
        return existing.Any(value => value.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))
            ? Result.Failure(new Error("metadata.name_conflict", $"The name already exists: {name.Trim()}.", ErrorKind.Conflict))
            : Result.Success();
    }

    private static ModUserIntent GetOrCreate(IReadOnlyDictionary<ModKey, ModUserIntent> intents, ModKey mod) => intents.TryGetValue(mod, out var intent)
        ? intent
        : new ModUserIntent(mod, false, false, null, null, null, new HashSet<TagId>(), null, new HashSet<WorkshopId>());
    private static void Store(IDictionary<ModKey, ModUserIntent> intents, ModUserIntent intent) { if (IsEmpty(intent)) intents.Remove(intent.Mod); else intents[intent.Mod] = intent; }
    private static bool IsEmpty(ModUserIntent intent) => !intent.IsActive && !intent.IsHidden && !intent.ExplicitOrder.HasValue && string.IsNullOrWhiteSpace(intent.ManualName) && intent.Category is null && intent.Tags.Count == 0 && string.IsNullOrWhiteSpace(intent.Note) && intent.IgnoredDependencies.Count == 0;
    private static IReadOnlyList<ModUserIntent> Order(IReadOnlyDictionary<ModKey, ModUserIntent> intents) => intents.Values.OrderBy(intent => intent.ExplicitOrder ?? int.MaxValue).ThenBy(intent => intent.Mod.LocationIdentity, StringComparer.Ordinal).ToArray();
    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static Result<ApplicationSettings> Failure(string code, string message) => Result<ApplicationSettings>.Failure(new Error(code, message, ErrorKind.Validation));
}
