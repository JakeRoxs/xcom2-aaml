using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Media.Imaging;

namespace AAML.Avalonia;

public sealed class FileImage : Image
{
    public static readonly StyledProperty<string?> SourcePathProperty =
        AvaloniaProperty.Register<FileImage, string?>(nameof(SourcePath));

    private Bitmap? loadedBitmap;

    public string? SourcePath
    {
        get => GetValue(SourcePathProperty);
        set => SetValue(SourcePathProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourcePathProperty) LoadSource(change.NewValue as string);
    }

    private void LoadSource(string? path)
    {
        Bitmap? replacement = null;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            try { replacement = new Bitmap(path); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { }
        }

        Source = replacement;
        loadedBitmap?.Dispose();
        loadedBitmap = replacement;
    }
}
