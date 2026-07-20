using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace GitFlick.Services.Updates;

/// <summary>
/// Source-generated serialization for the updater: the GitHub releases payload plus the on-disk
/// pending-update / cached-download state. No reflection, so the trim/AOT analyzers stay quiet.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<ReleaseInfo>))]
[JsonSerializable(typeof(PendingUpdateState))]
[JsonSerializable(typeof(CachedDownloadState))]
internal partial class UpdateJsonContext : JsonSerializerContext;
