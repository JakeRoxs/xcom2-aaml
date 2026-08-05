using AAML.Application.Ports;
using AAML.Application.Settings;

namespace AAML.Avalonia;

internal static class ShellStartupSettings
{
    public static async Task<ApplicationSettings?> LoadAsync(ISettingsRepository repository, CancellationToken cancellationToken)
    {
        try
        {
            var result = await repository.LoadAsync(cancellationToken).ConfigureAwait(false);
            return result.IsSuccess ? result.Value : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or OperationCanceledException)
        {
            return null;
        }
    }
}
