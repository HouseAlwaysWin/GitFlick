using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;

namespace GitFlick.Services.Updates;

/// <summary>
/// Self-update against the GitHub Releases API: check → download (with SHA-256 verification) → apply
/// (extract the win-x64 zip, hand off to a generated .bat that swaps the install in place, restart).
/// Any release can be installed, newer or older, which is the version-switching / rollback feature.
///
/// GitFlick keeps its settings in %APPDATA%\GitFlick (outside the install dir), so — unlike the
/// GimmeCapture original this is ported from — the swap never touches user config and needs no
/// config-migration step. Windows-only; single install per machine.
/// </summary>
public sealed partial class UpdateService : ObservableObject
{
    private const string ReleasesUrl = "https://api.github.com/repos/HouseAlwaysWin/GitFlick/releases";

    private readonly string _currentVersion;
    private readonly HttpClient _httpClient;
    private readonly ArtifactDownloader _artifactDownloader;
    private CancellationTokenSource? _downloadCancellation;

    [ObservableProperty]
    private double _downloadProgress;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private ArtifactDownloadStage _downloadStage;

    [ObservableProperty]
    private string? _lastErrorMessage;

    public UpdateService(string currentVersion, HttpClient? httpClient = null)
    {
        _currentVersion = currentVersion.TrimStart('v');
        _httpClient = httpClient ?? SharedHttpClient.Instance;
        _artifactDownloader = new ArtifactDownloader(_httpClient);
        LoadCachedDownload();
    }

    /// <summary>The version this running instance reports (tag without the leading <c>v</c>).</summary>
    public string CurrentVersion => _currentVersion;

    public string? DownloadedZipPath { get; private set; }

    public string? DownloadedVersion { get; private set; }

    // ---- Check --------------------------------------------------------------------------------

    /// <summary>The newest release strictly newer than the running version, or null if none / on error.</summary>
    public async Task<ReleaseInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var releases = await GetAvailableReleasesAsync(cancellationToken).ConfigureAwait(false);
            return releases.FirstOrDefault(r => IsNewerVersion(r.NormalizedVersion, _currentVersion));
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Update check failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>All installable releases (drafts/prereleases/asset-less dropped), newest first.</summary>
    public async Task<IReadOnlyList<ReleaseInfo>> GetAvailableReleasesAsync(CancellationToken cancellationToken = default)
    {
        var json = await _httpClient.GetStringAsync(ReleasesUrl, cancellationToken).ConfigureAwait(false);
        var releases = JsonSerializer.Deserialize(json, UpdateJsonContext.Default.ListReleaseInfo) ?? [];
        return FilterAndSortReleases(releases);
    }

    public static IReadOnlyList<ReleaseInfo> FilterAndSortReleases(IEnumerable<ReleaseInfo> releases) =>
        releases
            .Where(r => !r.Draft && !r.Prerelease && r.GetPreferredZipAsset() != null)
            .OrderByDescending(r => ParseVersionOrNull(r.NormalizedVersion) ?? new Version(0, 0, 0))
            .ThenByDescending(r => r.TagName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public bool IsUpdateDownloaded(ReleaseInfo release) =>
        DownloadedVersion == release.NormalizedVersion
        && !string.IsNullOrEmpty(DownloadedZipPath)
        && File.Exists(DownloadedZipPath);

    /// <summary>True if <paramref name="release"/> is strictly newer than the running version.</summary>
    public bool IsNewerThanCurrent(ReleaseInfo release) => IsNewerVersion(release.NormalizedVersion, _currentVersion);

    /// <summary>True if <paramref name="release"/> is the version currently running.</summary>
    public bool IsSameAsCurrent(ReleaseInfo release) =>
        string.Equals(release.NormalizedVersion, _currentVersion, StringComparison.OrdinalIgnoreCase);

    // ---- Download -----------------------------------------------------------------------------

    /// <summary>
    /// Downloads and verifies the release's zip into a fresh temp dir, returning the local path (or null
    /// on failure/cancel). A completed download is cached so a later install needs no re-download.
    /// </summary>
    public async Task<string?> DownloadUpdateAsync(ReleaseInfo release, CancellationToken cancellationToken = default)
    {
        if (IsDownloading)
        {
            return null;
        }

        var targetVersion = release.NormalizedVersion;
        if (DownloadedVersion == targetVersion
            && !string.IsNullOrEmpty(DownloadedZipPath)
            && File.Exists(DownloadedZipPath))
        {
            return DownloadedZipPath;
        }

        string? downloadDirectory = null;
        var downloadSucceeded = false;
        try
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _downloadCancellation = linkedCancellation;
            IsDownloading = true;
            DownloadProgress = 0;
            DownloadStage = ArtifactDownloadStage.Downloading;
            LastErrorMessage = null;

            var asset = release.GetPreferredZipAsset()
                ?? throw new InvalidOperationException("No suitable win-x64 zip asset found in the release.");
            var checksumAsset = release.GetChecksumAsset()
                ?? throw new InvalidOperationException("Release checksum manifest (SHA256SUMS.txt) is missing.");

            var checksumManifest = await _httpClient
                .GetStringAsync(checksumAsset.DownloadUrl, linkedCancellation.Token)
                .ConfigureAwait(false);
            var expectedSha256 = ParseChecksum(checksumManifest, asset.Name)
                ?? throw new InvalidDataException($"Checksum for {asset.Name} was not found in the manifest.");

            downloadDirectory = Path.Combine(Path.GetTempPath(), "GitFlick_Update_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(downloadDirectory);

            var descriptor = new ArtifactDescriptor(new Uri(asset.DownloadUrl), asset.Name, expectedSha256, asset.Size);
            var progress = new Progress<ArtifactDownloadProgress>(value =>
            {
                DownloadStage = value.Stage;
                DownloadProgress = value.Percentage;
            });

            var zipPath = await _artifactDownloader
                .DownloadAsync(descriptor, downloadDirectory, progress, linkedCancellation.Token)
                .ConfigureAwait(false);

            DownloadedZipPath = zipPath;
            DownloadedVersion = targetVersion;
            downloadSucceeded = true;
            PersistCachedDownload(targetVersion, zipPath);
            return zipPath;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Update download failed: {ex.Message}");
            LastErrorMessage = ex.Message;
            DownloadStage = ex is OperationCanceledException
                ? ArtifactDownloadStage.Cancelled
                : ArtifactDownloadStage.Failed;
            return null;
        }
        finally
        {
            _downloadCancellation = null;
            IsDownloading = false;
            if (!downloadSucceeded && downloadDirectory != null)
            {
                TryDeleteDirectory(downloadDirectory);
            }
        }
    }

    public void CancelDownload()
    {
        try
        {
            _downloadCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    // ---- Apply --------------------------------------------------------------------------------

    /// <summary>
    /// Extracts the zip, records a pending-update marker, launches the detached swap script, and exits.
    /// On any pre-exit failure it surfaces the message via <see cref="LastErrorMessage"/> and stays up.
    /// </summary>
    public void ApplyUpdate(string zipPath, string targetVersion)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                LastErrorMessage = "Automatic update is only supported on Windows.";
                return;
            }

            var currentExePath = RuntimePathProvider.GetExecutablePath();
            var appDir = Path.GetDirectoryName(currentExePath) ?? RuntimePathProvider.GetExecutableDirectory();
            var currentProcessId = Environment.ProcessId;
            var parentDir = Path.GetDirectoryName(zipPath)!;
            var tempExtractDir = Path.Combine(parentDir, "extract-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempExtractDir);

            SafeZipExtractor.ExtractToDirectory(zipPath, tempExtractDir, overwriteFiles: true);

            var currentExeName = Path.GetFileName(currentExePath);
            var extractSourceDir = ResolveExtractSourceDirectory(tempExtractDir, currentExeName);
            var expectedExePath = Path.Combine(appDir, currentExeName);

            WritePendingUpdateState(new PendingUpdateState
            {
                TargetVersion = targetVersion.TrimStart('v'),
                ExpectedExePath = expectedExePath,
                AppDirectory = appDir,
            });

            var scriptPath = Path.Combine(Path.GetTempPath(), "GitFlick_Update.bat");
            var script = BuildUpdateScript(extractSourceDir, appDir, parentDir, expectedExePath, currentProcessId);
            File.WriteAllText(scriptPath, script, Encoding.Default);

            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });

            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Update apply failed: {ex}");
            LastErrorMessage = ex.Message;
        }
    }

    /// <summary>
    /// The detached batch script that finishes the swap: wait for this process to exit, back up the
    /// install, robocopy the new build over it (retried), verify the exe, restart, self-delete. On any
    /// failure it rolls the backup back and relaunches, so a botched update can't brick the install.
    /// Pure string builder, so it is unit-testable.
    /// </summary>
    public static string BuildUpdateScript(
        string extractSourceDir,
        string appDir,
        string parentDir,
        string expectedExePath,
        int currentProcessId)
    {
        var appBackupDir = Path.Combine(parentDir, "app-backup");
        var pendingUpdateStatePath = PendingUpdateStatePath;

        return $@"
@echo off
setlocal EnableExtensions EnableDelayedExpansion
set ""WAIT_COUNT=0""
:wait_for_exit
tasklist /FI ""PID eq {currentProcessId}"" | find ""{currentProcessId}"" > nul
if not errorlevel 1 (
  if !WAIT_COUNT! GEQ 60 goto wait_failed
  set /a WAIT_COUNT+=1
  timeout /t 1 /nobreak > nul
  goto wait_for_exit
)
if not exist ""{extractSourceDir}"" goto update_failed
if exist ""{appBackupDir}"" rd /s /q ""{appBackupDir}""
mkdir ""{appBackupDir}""
robocopy ""{appDir.TrimEnd('\\')}"" ""{appBackupDir.TrimEnd('\\')}"" /E /R:2 /W:1 /NFL /NDL /NJH /NJS /NP > nul
if !ERRORLEVEL! GEQ 8 goto update_failed
set ""COPY_COUNT=0""
:copy_retry
robocopy ""{extractSourceDir.TrimEnd('\\')}"" ""{appDir.TrimEnd('\\')}"" /E /R:2 /W:1 /NFL /NDL /NJH /NJS /NP > nul
set ""ROBOCOPY_EXIT=!ERRORLEVEL!""
if !ROBOCOPY_EXIT! GEQ 8 (
  if !COPY_COUNT! GEQ 5 goto rollback
  set /a COPY_COUNT+=1
  timeout /t 1 /nobreak > nul
  goto copy_retry
)
if not exist ""{expectedExePath}"" goto rollback
rd /s /q ""{parentDir.TrimEnd('\\')}""
start """" ""{expectedExePath}""
del ""%~f0""
exit /b 0

:rollback
robocopy ""{appBackupDir.TrimEnd('\\')}"" ""{appDir.TrimEnd('\\')}"" /MIR /R:2 /W:1 /NFL /NDL /NJH /NJS /NP > nul

:update_failed
if exist ""{pendingUpdateStatePath}"" del /f /q ""{pendingUpdateStatePath}""
if exist ""{expectedExePath}"" start """" ""{expectedExePath}""
exit /b 1

:wait_failed
if exist ""{pendingUpdateStatePath}"" del /f /q ""{pendingUpdateStatePath}""
exit /b 1
";
    }

    // ---- Startup verification -----------------------------------------------------------------

    /// <summary>
    /// Called on launch: if a pending-update marker exists, confirm the running exe path + version match
    /// what the update promised, then clear the marker (and cached download). Mismatch = failure report.
    /// </summary>
    public static UpdateVerificationResult VerifyPendingUpdateOnStartup(string currentVersion, string currentExePath)
    {
        var state = ReadPendingUpdateState();
        if (state == null)
        {
            return UpdateVerificationResult.None();
        }

        // The swap already ran, so this is a one-shot check: consume the marker up front either way,
        // so a mismatch can't make every subsequent launch re-report the same stale update.
        ClearPendingUpdateState();

        var normalizedCurrentVersion = currentVersion.TrimStart('v');
        var normalizedCurrentPath = Path.GetFullPath(currentExePath);
        var normalizedExpectedPath = Path.GetFullPath(state.ExpectedExePath);

        if (!string.Equals(normalizedCurrentPath, normalizedExpectedPath, StringComparison.OrdinalIgnoreCase))
        {
            var message = $"Expected executable: {normalizedExpectedPath}\nActual executable: {normalizedCurrentPath}";
            WriteFailedVerificationReport(state, message);
            return UpdateVerificationResult.Failure(state, message);
        }

        if (!string.Equals(normalizedCurrentVersion, state.TargetVersion, StringComparison.OrdinalIgnoreCase))
        {
            var message = $"Expected version: {state.TargetVersion}\nActual version: {normalizedCurrentVersion}";
            WriteFailedVerificationReport(state, message);
            return UpdateVerificationResult.Failure(state, message);
        }

        ClearCachedDownloadState();
        return UpdateVerificationResult.Success(state);
    }

    // ---- Helpers ------------------------------------------------------------------------------

    internal static string? ParseChecksum(string manifest, string fileName)
    {
        foreach (var line in manifest.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2
                && parts[0].Length == 64
                && string.Equals(parts[1].TrimStart('*'), fileName, StringComparison.OrdinalIgnoreCase))
            {
                return parts[0].ToLowerInvariant();
            }
        }

        return null;
    }

    internal static bool IsNewerVersion(string newVer, string currentVer)
    {
        var newVersion = ParseVersionOrNull(newVer);
        var currentVersion = ParseVersionOrNull(currentVer);
        if (newVersion != null && currentVersion != null)
        {
            return newVersion > currentVersion;
        }

        return string.Compare(newVer, currentVer, StringComparison.OrdinalIgnoreCase) > 0;
    }

    private static Version? ParseVersionOrNull(string version) =>
        Version.TryParse(version, out var parsed) ? parsed : null;

    // The zip may extract straight to the exe, or into a single nested folder. Find where the exe landed.
    private static string ResolveExtractSourceDirectory(string extractRoot, string expectedExeName)
    {
        if (File.Exists(Path.Combine(extractRoot, expectedExeName)))
        {
            return extractRoot;
        }

        foreach (var nested in Directory.GetDirectories(extractRoot))
        {
            if (File.Exists(Path.Combine(nested, expectedExeName)))
            {
                return nested;
            }
        }

        return extractRoot;
    }

    // ---- On-disk state (fixed %APPDATA%\GitFlick, alongside settings.json) ---------------------

    private static string StorageDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GitFlick");

    private static string PendingUpdateStatePath => Path.Combine(StorageDirectory, "pending-update.json");

    private static string CachedDownloadStatePath => Path.Combine(StorageDirectory, "cached-download.json");

    private static string FailedVerificationReportPath => Path.Combine(StorageDirectory, "update-failed.txt");

    private static void WritePendingUpdateState(PendingUpdateState state)
    {
        Directory.CreateDirectory(StorageDirectory);
        File.WriteAllText(PendingUpdateStatePath,
            JsonSerializer.Serialize(state, UpdateJsonContext.Default.PendingUpdateState));
    }

    private static PendingUpdateState? ReadPendingUpdateState()
    {
        try
        {
            return File.Exists(PendingUpdateStatePath)
                ? JsonSerializer.Deserialize(File.ReadAllText(PendingUpdateStatePath), UpdateJsonContext.Default.PendingUpdateState)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void ClearPendingUpdateState()
    {
        try
        {
            if (File.Exists(PendingUpdateStatePath))
            {
                File.Delete(PendingUpdateStatePath);
            }
        }
        catch
        {
        }
    }

    private void LoadCachedDownload()
    {
        try
        {
            var state = ReadCachedDownloadState();
            if (state != null
                && !string.IsNullOrEmpty(state.ZipPath)
                && File.Exists(state.ZipPath)
                && IsNewerVersion(state.TargetVersion, _currentVersion))
            {
                DownloadedZipPath = state.ZipPath;
                DownloadedVersion = state.TargetVersion;
            }
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Loading cached update download failed: {ex.Message}");
        }
    }

    private void PersistCachedDownload(string targetVersion, string zipPath)
    {
        try
        {
            Directory.CreateDirectory(StorageDirectory);
            File.WriteAllText(CachedDownloadStatePath, JsonSerializer.Serialize(
                new CachedDownloadState { TargetVersion = targetVersion, ZipPath = zipPath },
                UpdateJsonContext.Default.CachedDownloadState));
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Persisting cached update download failed: {ex.Message}");
        }
    }

    private static CachedDownloadState? ReadCachedDownloadState()
    {
        try
        {
            return File.Exists(CachedDownloadStatePath)
                ? JsonSerializer.Deserialize(File.ReadAllText(CachedDownloadStatePath), UpdateJsonContext.Default.CachedDownloadState)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void ClearCachedDownloadState()
    {
        try
        {
            if (!File.Exists(CachedDownloadStatePath))
            {
                return;
            }

            // Best-effort: also drop the cached installer's temp directory.
            try
            {
                var state = ReadCachedDownloadState();
                var zipDir = string.IsNullOrEmpty(state?.ZipPath) ? null : Path.GetDirectoryName(state!.ZipPath);
                if (!string.IsNullOrEmpty(zipDir) && Directory.Exists(zipDir))
                {
                    Directory.Delete(zipDir, recursive: true);
                }
            }
            catch
            {
            }

            File.Delete(CachedDownloadStatePath);
        }
        catch
        {
        }
    }

    private static void WriteFailedVerificationReport(PendingUpdateState state, string failureMessage)
    {
        try
        {
            Directory.CreateDirectory(StorageDirectory);
            var report = $"Update verification failed at {DateTimeOffset.Now:O}{Environment.NewLine}"
                       + $"TargetVersion: {state.TargetVersion}{Environment.NewLine}"
                       + $"ExpectedExePath: {state.ExpectedExePath}{Environment.NewLine}"
                       + failureMessage;
            File.WriteAllText(FailedVerificationReportPath, report);
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch
        {
        }
    }
}
