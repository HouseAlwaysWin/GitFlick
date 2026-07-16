using System.Threading;
using System.Threading.Tasks;

namespace GitFlick.Services;

/// <summary>Turns a staged diff into a commit message via a local model. Throws with a user-facing message on failure.</summary>
public interface ICommitMessageGenerator
{
    Task<string> GenerateAsync(string diff, string baseUrl, string model, CancellationToken cancellationToken = default);
}
