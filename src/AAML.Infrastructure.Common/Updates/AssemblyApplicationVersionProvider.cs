using System.Reflection;
using AAML.Application.Common;
using AAML.Application.Ports;

namespace AAML.Infrastructure.Common.Updates;

public sealed class AssemblyApplicationVersionProvider(Assembly assembly) : IApplicationVersionProvider
{
    public Result<string> GetCurrentVersion()
    {
        var value = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? assembly.GetName().Version?.ToString();
        return string.IsNullOrWhiteSpace(value) ? Result<string>.Failure(new Error("update.version_unavailable", "The application version is unavailable.", ErrorKind.Unavailable)) : Result<string>.Success(value);
    }
}
