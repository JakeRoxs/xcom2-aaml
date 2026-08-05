using AAML.Application.Configurations;
using AAML.Domain.Mods;
using FluentAssertions;

namespace AAML.Application.Tests;

[TestClass]
public sealed class ConfigurationEditorStateTests
{
    private static readonly ConfigurationDocumentId Id = new(new ModKey(ModSource.Manual, "fixture/mod"), "Config/XComFixture.ini");
    private static readonly ConfigurationTextFormat Format = new(ConfigurationEncoding.Utf8, NewLineStyle.CrLf);

    [TestMethod]
    public void LoadEditRevertAndSelection_HaveExplicitDirtySemantics()
    {
        var state = ConfigurationEditorState.Loaded(new ConfigurationFileVersion(Id, "[Section]\r\nValue=A\r\n", Format, "r1"));

        state.IsDirty.Should().BeFalse();
        state.ReplaceText("changed").IsDirty.Should().BeTrue();
        state.ReplaceText("changed").ReplaceText(state.Baseline.Text).IsDirty.Should().BeFalse();
        state.Select(2, 4).IsDirty.Should().BeFalse();
    }

    [TestMethod]
    public void Snapshot_DoesNotChangeDiskBaseline()
    {
        var state = ConfigurationEditorState.Loaded(new ConfigurationFileVersion(Id, "disk", Format, "r1"));

        var applied = state.ApplySnapshot(new SavedConfigurationSnapshot(Id, "snapshot", Format));

        applied.Text.Should().Be("snapshot");
        applied.Baseline.Text.Should().Be("disk");
        applied.IsDirty.Should().BeTrue();
    }

    [TestMethod]
    public void SuccessfulSave_AdvancesBaselineAndClearsDirty()
    {
        var state = ConfigurationEditorState.Loaded(new ConfigurationFileVersion(Id, "disk", Format, "r1")).ReplaceText("edited");

        var saved = state.AcceptSave(new ConfigurationSaveReceipt("r2", true));

        saved.IsDirty.Should().BeFalse();
        saved.Baseline.Text.Should().Be("edited");
        saved.Baseline.Revision.Should().Be("r2");
    }
}
