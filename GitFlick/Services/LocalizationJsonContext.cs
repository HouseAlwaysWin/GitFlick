using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace GitFlick.Services;

/// <summary>
/// Source-generated deserialization for the flat localization bundles. Keeping the reflection-based
/// <c>Deserialize&lt;Dictionary&lt;…&gt;&gt;</c> out of the picture is what keeps the NativeAOT path open
/// (a plain reflection deserialize trips IL2026/IL3050).
/// </summary>
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class LocalizationJsonContext : JsonSerializerContext;
