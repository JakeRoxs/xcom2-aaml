using AAML.Application.Common;
using AAML.Domain.Profiles;

namespace AAML.Application.Profiles;

public interface IProfileInterchange
{
    Task<Result<string>> ExportAsync(ProfileId id, CancellationToken cancellationToken);
    Task<Result<ModProfile>> ImportAsync(string document, CancellationToken cancellationToken);
}
