using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitFlick.Models;
using GitFlick.Services;

namespace GitFlick.ViewModels;

/// <summary>How a single conflicted path can be resolved — drives which controls the window shows.</summary>
public enum ConflictFileKind
{
    /// <summary>Both sides changed content: 3-way markers, editable, take-a-side works.</summary>
    Text,

    /// <summary>Both changed but the content is binary: no editing, only take-a-side.</summary>
    Binary,

    /// <summary>One side modified, the other deleted (or added): keep-the-file vs accept-the-deletion.</summary>
    ModifyDelete,

    /// <summary>Anything else (e.g. both-deleted): only "mark resolved" — punt the detail to the CLI.</summary>
    Other,
}

/// <summary>One unresolved conflicted path in the resolver's list.</summary>
public sealed record ConflictFile(string Path, ConflictFileKind Kind)
{
    public string Name
    {
        get
        {
            var slash = Path.LastIndexOfAny(['/', '\\']);
            return slash >= 0 && slash < Path.Length - 1 ? Path[(slash + 1)..] : Path;
        }
    }
}

/// <summary>
/// Backs the conflict-resolution window: the list of unresolved paths, the editor for the selected one,
/// and the take-a-side / mark-resolved / abort / complete actions. Works for any
/// <see cref="ConflictOperation"/> (merge, cherry-pick, revert, rebase) — see docs/adr/0001.
/// </summary>
public partial class ConflictResolverViewModel : ObservableObject
{
    private readonly IGitService _git;
    private readonly string _repoPath;

    private static LocalizationService Loc => LocalizationService.Instance;

    public ConflictResolverViewModel(IGitService git, string repoPath, ConflictOperation operation)
    {
        _git = git;
        _repoPath = repoPath;
        Operation = operation;
    }

    /// <summary>Which operation is being resolved — decides the verbs and the window's labels.</summary>
    public ConflictOperation Operation { get; }

    /// <summary>Raised when the operation ends (completed or aborted) so the view can close the window.</summary>
    public event EventHandler? Finished;

    /// <summary>Set by the view: confirms an abort (it throws away resolved work). Null = no prompt.</summary>
    public Func<Task<bool>>? ConfirmAbort { get; set; }

    /// <summary>Set by the view: pushes new editor text in (AvaloniaEdit's Document isn't a bind target).</summary>
    public Action<string>? SetEditorText { get; set; }

    /// <summary>Set by the view: reads the (possibly edited) editor text back out.</summary>
    public Func<string>? GetEditorText { get; set; }

    public ObservableCollection<ConflictFile> Conflicts { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTextConflict))]
    [NotifyPropertyChangedFor(nameof(IsModifyDelete))]
    [NotifyPropertyChangedFor(nameof(ShowAcceptDeletion))]
    [NotifyPropertyChangedFor(nameof(CanTakeSide))]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(ResolutionHint))]
    public partial ConflictFile? SelectedConflict { get; set; }

    /// <summary>Top marker label ("HEAD", a branch…) — the caption of the "take this side" button.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TakeOursCaption))]
    public partial string OursLabel { get; set; } = string.Empty;

    /// <summary>Bottom marker label — the caption of the other "take this side" button.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TakeTheirsCaption))]
    public partial string TheirsLabel { get; set; } = string.Empty;

    /// <summary>"Take HEAD" etc. from the marker label, or a plain "take the top side" when none was found.</summary>
    public string TakeOursCaption => OursLabel.Length > 0
        ? string.Format(Loc["Conflict_TakeSide"], OursLabel)
        : Loc["Conflict_TakeTop"];

    public string TakeTheirsCaption => TheirsLabel.Length > 0
        ? string.Format(Loc["Conflict_TakeSide"], TheirsLabel)
        : Loc["Conflict_TakeBottom"];

    /// <summary>The selected file still has `&lt;&lt;&lt;&lt;&lt;&lt;&lt;` markers — warn before resolving.</summary>
    [ObservableProperty]
    public partial bool HasStaleMarkers { get; set; }

    [ObservableProperty]
    public partial string? StatusText { get; set; }

    public bool HasSelection => SelectedConflict is not null;
    public bool IsTextConflict => SelectedConflict?.Kind == ConflictFileKind.Text;
    public bool IsModifyDelete => SelectedConflict?.Kind == ConflictFileKind.ModifyDelete;

    /// <summary>modify/delete keeps or deletes; both-deleted can only accept the deletion (git rm).</summary>
    public bool ShowAcceptDeletion => SelectedConflict?.Kind is ConflictFileKind.ModifyDelete or ConflictFileKind.Other;

    public bool CanTakeSide => SelectedConflict?.Kind is ConflictFileKind.Text or ConflictFileKind.Binary;

    /// <summary>A line explaining a non-text conflict (binary, modify/delete, other); empty for text.</summary>
    public string ResolutionHint => SelectedConflict?.Kind switch
    {
        ConflictFileKind.ModifyDelete => Loc["Conflict_ModifyDeleteHint"],
        ConflictFileKind.Binary => Loc["Conflict_BinaryHint"],
        ConflictFileKind.Other => Loc["Conflict_OtherHint"],
        _ => string.Empty,
    };

    /// <summary>Still conflicts left — the merge can't be completed yet.</summary>
    public bool HasUnresolved => Conflicts.Count > 0;

    /// <summary>Rebuilds the conflict list from git status and loads the (kept or first) selection.</summary>
    public async Task LoadAsync()
    {
        var keep = SelectedConflict?.Path;

        GitStatus status;
        try
        {
            status = await _git.GetStatusAsync(_repoPath);
        }
        catch (GitException ex)
        {
            StatusText = ex.Message;
            return;
        }

        var conflicts = status.Entries
            .Where(e => e.Kind == GitChangeKind.Unmerged)
            .Select(e => new ConflictFile(e.Path, Classify(e)))
            .ToList();

        Conflicts.Clear();
        foreach (var c in conflicts)
        {
            Conflicts.Add(c);
        }

        OnPropertyChanged(nameof(HasUnresolved));

        SelectedConflict = Conflicts.FirstOrDefault(c => c.Path == keep) ?? Conflicts.FirstOrDefault();

        // Nothing left: the operation is fully resolved and ready to complete.
        if (Conflicts.Count == 0)
        {
            LoadFileIntoEditor(null);
        }
    }

    // Classify from git's two-letter code, then reclassify a "both-modified" file as binary if it
    // sniffs binary — the editor is meaningless for those.
    private ConflictFileKind Classify(GitStatusEntry entry)
    {
        var kind = entry.UnmergedCode switch
        {
            "UU" or "AA" => ConflictFileKind.Text,
            "UD" or "DU" or "UA" or "AU" => ConflictFileKind.ModifyDelete,
            "DD" => ConflictFileKind.Other,
            _ => ConflictFileKind.Text,
        };

        if (kind == ConflictFileKind.Text)
        {
            try
            {
                var full = Path.Combine(_repoPath, entry.Path);
                if (File.Exists(full))
                {
                    var head = File.ReadAllBytes(full);
                    if (ConflictMarkers.LooksBinary(head))
                    {
                        kind = ConflictFileKind.Binary;
                    }
                }
            }
            catch (IOException)
            {
                // Can't read it — leave it as Text; the editor just shows whatever loads.
            }
        }

        return kind;
    }

    partial void OnSelectedConflictChanged(ConflictFile? value) => LoadFileIntoEditor(value);

    private void LoadFileIntoEditor(ConflictFile? file)
    {
        if (file is null || file.Kind is not ConflictFileKind.Text)
        {
            SetEditorText?.Invoke(string.Empty);
            OursLabel = string.Empty;
            TheirsLabel = string.Empty;
            HasStaleMarkers = false;
            return;
        }

        string text;
        try
        {
            text = File.ReadAllText(Path.Combine(_repoPath, file.Path));
        }
        catch (IOException ex)
        {
            StatusText = ex.Message;
            return;
        }

        SetEditorText?.Invoke(text);
        var (ours, theirs) = ConflictMarkers.Labels(text);
        OursLabel = ours;
        TheirsLabel = theirs;
        HasStaleMarkers = ConflictMarkers.HasMarkers(text);
    }

    /// <summary>Writes the editor's current text back to the working file (no staging).</summary>
    [RelayCommand]
    private void Save()
    {
        if (SelectedConflict is not { Kind: ConflictFileKind.Text } file || GetEditorText is null)
        {
            return;
        }

        WriteEditor(file);
    }

    /// <summary>Saves the edited text, then stages it to mark the conflict resolved.</summary>
    [RelayCommand]
    private async Task MarkResolved()
    {
        if (SelectedConflict is not { } file)
        {
            return;
        }

        if (file.Kind is ConflictFileKind.Text)
        {
            WriteEditor(file);
        }

        await Run(() => _git.StageAsync(_repoPath, file.Path));
    }

    [RelayCommand]
    private Task TakeOurs() => TakeSide(ours: true);

    [RelayCommand]
    private Task TakeTheirs() => TakeSide(ours: false);

    private async Task TakeSide(bool ours)
    {
        if (SelectedConflict is not { } file || !CanTakeSide)
        {
            return;
        }

        var take = ours ? _git.TakeOursAsync(_repoPath, file.Path) : _git.TakeTheirsAsync(_repoPath, file.Path);
        if ((await take).Succeeded)
        {
            await Run(() => _git.StageAsync(_repoPath, file.Path));   // taking a side resolves it
        }
    }

    /// <summary>modify/delete: keep the working file (stage it as-is).</summary>
    [RelayCommand]
    private async Task KeepFile()
    {
        if (SelectedConflict is { } file)
        {
            await Run(() => _git.StageAsync(_repoPath, file.Path));
        }
    }

    /// <summary>modify/delete: accept the deletion (git rm).</summary>
    [RelayCommand]
    private async Task AcceptDeletion()
    {
        if (SelectedConflict is { } file)
        {
            await Run(() => _git.RemoveFileAsync(_repoPath, file.Path));
        }
    }

    [RelayCommand]
    private async Task AbortMerge()
    {
        if (ConfirmAbort is not null && !await ConfirmAbort())
        {
            return;
        }

        var result = await _git.AbortOperationAsync(_repoPath, Operation);
        StatusText = result.Succeeded ? null : result.FailureMessage;
        if (result.Succeeded)
        {
            Finished?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand]
    private async Task CompleteMerge()
    {
        if (HasUnresolved)
        {
            StatusText = Loc["Conflict_StillUnresolved"];
            return;
        }

        var result = await _git.ContinueOperationAsync(_repoPath, Operation);
        if (!result.Succeeded)
        {
            StatusText = result.FailureMessage;
            return;
        }

        // A rebase may have stopped again on the next commit — reload; only finish when git says the
        // whole operation is done.
        var still = await _git.GetConflictOperationAsync(_repoPath);
        if (still == ConflictOperation.None)
        {
            Finished?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            await LoadAsync();
        }
    }

    private void WriteEditor(ConflictFile file)
    {
        if (GetEditorText is null)
        {
            return;
        }

        try
        {
            File.WriteAllText(Path.Combine(_repoPath, file.Path), GetEditorText());
        }
        catch (IOException ex)
        {
            StatusText = ex.Message;
        }
    }

    // Run a git action, surface any failure, then rebuild the list (the resolved file drops out).
    private async Task Run(Func<Task<GitCommandResult>> action)
    {
        try
        {
            var result = await action();
            StatusText = result.Succeeded ? null : result.FailureMessage;
        }
        catch (GitException ex)
        {
            StatusText = ex.Message;
        }

        await LoadAsync();
    }
}
