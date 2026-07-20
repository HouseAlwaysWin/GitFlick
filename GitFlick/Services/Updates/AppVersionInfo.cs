using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace GitFlick.Services.Updates;

/// <summary>
/// The running app's version. The release workflow stamps <c>InformationalVersion</c> from the git tag
/// (see .github/workflows/release.yml), so this matches the release the user is running.
/// </summary>
public static class AppVersionInfo
{
    // Reading assembly attributes is trim/AOT-safe for the executing assembly's own metadata.
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Reads only the executing assembly's own version attributes, which are always preserved.")]
    public static string CurrentVersion { get; } = Resolve();

    private static string Resolve()
    {
        var assembly = Assembly.GetExecutingAssembly();

        // InformationalVersion can carry a "+<gitsha>" build-metadata suffix; drop it.
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            return informational.Split('+')[0];
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
