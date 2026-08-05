using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace AAML.Avalonia;

public sealed class IniColorizingTransformer(IBrush comment, IBrush section, IBrush separator) : DocumentColorizingTransformer
{
    protected override void ColorizeLine(DocumentLine line)
    {
        var text = CurrentContext.Document.GetText(line);
        var first = text.AsSpan().TrimStart();
        if (first.StartsWith(";", StringComparison.Ordinal) || first.StartsWith("#", StringComparison.Ordinal))
        {
            ChangeLinePart(line.Offset, line.EndOffset, element => element.TextRunProperties.SetForegroundBrush(comment));
            return;
        }
        if (first.StartsWith("[", StringComparison.Ordinal) && first.Contains(']'))
            ChangeLinePart(line.Offset, line.EndOffset, element => element.TextRunProperties.SetForegroundBrush(section));
        var equals = text.IndexOf('=', StringComparison.Ordinal);
        if (equals >= 0) ChangeLinePart(line.Offset + equals, line.Offset + equals + 1, element => element.TextRunProperties.SetForegroundBrush(separator));
    }
}
