using System.Threading;
using System.Threading.Tasks;

namespace GitFlick.Services;

/// <summary>
/// Turns a staged diff into a commit message. Each engine reads its own configuration from
/// settings. Throws with a user-facing message on failure.
/// </summary>
public interface ICommitMessageGenerator
{
    Task<string> GenerateAsync(string diff, CancellationToken cancellationToken = default);
}
