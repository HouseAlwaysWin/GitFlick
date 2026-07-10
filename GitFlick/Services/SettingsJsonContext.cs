using System.Text.Json.Serialization;
using GitFlick.Models;

namespace GitFlick.Services;

/// <summary>
/// Source-generated serialization: no reflection, so the NativeAOT path stays open.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal partial class SettingsJsonContext : JsonSerializerContext;
