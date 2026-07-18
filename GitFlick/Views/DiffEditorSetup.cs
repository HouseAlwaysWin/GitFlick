using AvaloniaEdit;
using AvaloniaEdit.TextMate;
using TextMateSharp.Grammars;

namespace GitFlick.Views;

/// <summary>Shared diff-viewer wiring: the +/- line-background renderer plus the TextMate diff grammar.</summary>
internal static class DiffEditorSetup
{
    public static void Apply(TextEditor editor)
    {
        editor.TextArea.TextView.BackgroundRenderers.Add(new DiffLineBackgroundRenderer());

        var registryOptions = new RegistryOptions(ThemeName.DarkPlus);
        var installation = editor.InstallTextMate(registryOptions);
        installation.SetGrammar(registryOptions.GetScopeByLanguageId("diff"));
    }
}
