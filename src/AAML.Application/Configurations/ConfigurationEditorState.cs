namespace AAML.Application.Configurations;

/// <summary>Pure editor state whose dirty flag compares current text to the accepted disk baseline.</summary>
public sealed record ConfigurationEditorState(
    ConfigurationFileVersion Baseline,
    string Text,
    int SelectionStart,
    int SelectionLength)
{
    public bool IsDirty => !string.Equals(Text, Baseline.Text, StringComparison.Ordinal);

    public static ConfigurationEditorState Loaded(ConfigurationFileVersion version) => new(version, version.Text, 0, 0);

    public ConfigurationEditorState ReplaceText(string text) => this with { Text = text ?? throw new ArgumentNullException(nameof(text)) };

    public ConfigurationEditorState Select(int start, int length) => this with
    {
        SelectionStart = Math.Clamp(start, 0, Text.Length),
        SelectionLength = Math.Clamp(length, 0, Text.Length - Math.Clamp(start, 0, Text.Length))
    };

    public ConfigurationEditorState ApplySnapshot(SavedConfigurationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Id != Baseline.Id) throw new ArgumentException("Snapshot belongs to another document.", nameof(snapshot));
        return this with { Text = snapshot.Text, SelectionStart = 0, SelectionLength = 0 };
    }

    public ConfigurationEditorState AcceptSave(ConfigurationSaveReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return this with { Baseline = Baseline with { Text = Text, Revision = receipt.Revision, Format = receipt.Format ?? Baseline.Format } };
    }
}
