using Avalonia;
using Avalonia.Controls.Primitives;
using AvaloniaEdit.Rendering;

namespace AAML.Avalonia;

public sealed class SynchronizedTextViewScroll : IDisposable
{
    private readonly TextView left;
    private readonly TextView right;
    private bool synchronizing;

    public SynchronizedTextViewScroll(TextView left, TextView right)
    {
        this.left = left; this.right = right;
        left.ScrollOffsetChanged += LeftChanged;
        right.ScrollOffsetChanged += RightChanged;
    }

    public void Dispose()
    {
        left.ScrollOffsetChanged -= LeftChanged;
        right.ScrollOffsetChanged -= RightChanged;
    }

    private void LeftChanged(object? sender, EventArgs e) => Synchronize(left, right);
    private void RightChanged(object? sender, EventArgs e) => Synchronize(right, left);
    private void Synchronize(TextView source, TextView target)
    {
        if (synchronizing) return;
        synchronizing = true;
        try { ((IScrollable)target).Offset = new Vector(target.ScrollOffset.X, source.ScrollOffset.Y); }
        finally { synchronizing = false; }
    }
}
