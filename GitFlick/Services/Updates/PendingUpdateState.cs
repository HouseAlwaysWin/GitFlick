using System;

namespace GitFlick.Services.Updates;

/// <summary>
/// Written to disk just before the app exits to let the helper script swap it in place. On the next
/// launch the updater confirms the running exe path + version match what was promised, then clears it.
/// </summary>
public sealed class PendingUpdateState
{
    public string TargetVersion { get; set; } = string.Empty;
    public string ExpectedExePath { get; set; } = string.Empty;
    public string AppDirectory { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A completed download remembered across restarts, so "Later" → install needs no re-download. Ignored
/// if the file is gone or no longer newer than the running version.
/// </summary>
public sealed class CachedDownloadState
{
    public string TargetVersion { get; set; } = string.Empty;
    public string ZipPath { get; set; } = string.Empty;
    public DateTimeOffset CachedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Outcome of the startup check for a just-applied update.</summary>
public sealed class UpdateVerificationResult
{
    public bool HasPendingUpdate { get; init; }
    public bool IsSuccess { get; init; }
    public string? FailureMessage { get; init; }
    public PendingUpdateState? State { get; init; }

    public static UpdateVerificationResult None() => new() { HasPendingUpdate = false, IsSuccess = true };

    public static UpdateVerificationResult Success(PendingUpdateState state) =>
        new() { HasPendingUpdate = true, IsSuccess = true, State = state };

    public static UpdateVerificationResult Failure(PendingUpdateState? state, string message) =>
        new() { HasPendingUpdate = true, IsSuccess = false, State = state, FailureMessage = message };
}
