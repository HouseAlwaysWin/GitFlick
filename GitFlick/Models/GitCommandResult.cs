using System;

namespace GitFlick.Models;

/// <summary>The outcome of one git invocation: exit code plus captured streams.</summary>
public sealed record GitCommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;

    /// <summary>stderr if present, otherwise stdout — whatever git used to explain a failure.</summary>
    public string FailureMessage =>
        StandardError.Trim() is { Length: > 0 } err ? err : StandardOutput.Trim();

    /// <summary>
    /// Throws a <see cref="GitException"/> describing <paramref name="operation"/> when the command
    /// failed, so hard-fail callers don't each repeat the check. Commands that tolerate a non-zero
    /// exit (soft-fail, e.g. an unborn HEAD) should test <see cref="Succeeded"/> themselves instead.
    /// </summary>
    public void EnsureSuccess(string operation)
    {
        if (!Succeeded)
        {
            throw new GitException($"{operation} failed: {FailureMessage}");
        }
    }
}

/// <summary>Thrown when git cannot be started or produced output we cannot make sense of.</summary>
public sealed class GitException : Exception
{
    public GitException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
