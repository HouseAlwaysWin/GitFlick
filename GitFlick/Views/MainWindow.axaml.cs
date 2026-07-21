using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.TextMate;
using GitFlick.Models;
using GitFlick.Services;
using GitFlick.ViewModels;
using TextMateSharp.Grammars;

namespace GitFlick.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _observedMain;
    private WorkspaceViewModel? _observedWorkspace;

    /// <summary>Remembers the History file-list height across mode switches (it collapses in Changes mode).</summary>
    private GridLength _commitFilesHeight = new(150);

    /// <summary>Shorthand for the app string table. Dialogs rebuild per open, so they pick up the current language.</summary>
    private static LocalizationService Loc => LocalizationService.Instance;

    public MainWindow()
    {
        InitializeComponent();

        // Palette navigation is handled on the tunnel (preview) so the window sees the keys
        // before whatever control has focus — the search box OR the list after a click.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        // Give memory back to the OS once dismissed to the tray, and stand down the moment we're
        // summoned again. Catches every hide path (Esc, X, hotkey) via the one IsVisible signal.
        PropertyChanged += OnWindowVisibilityChanged;

        // Mouse users expect a double-click to open a repo / move a file across the staging line.
        RepoList.DoubleTapped += (_, _) => (DataContext as MainViewModel)?.OpenSelected();
        UnstagedList.DoubleTapped += (_, _) => Workspace?.StageCommand.Execute(Workspace.SelectedUnstagedFile);
        StagedList.DoubleTapped += (_, _) => Workspace?.UnstageCommand.Execute(Workspace.SelectedStagedFile);

        // Multi-select: Enter (or the context menu) acts on every selected file at once.
        UnstagedList.KeyDown += (_, e) => { if (e.Key == Key.Enter) { StageSelectedFiles(); e.Handled = true; } };
        StagedList.KeyDown += (_, e) => { if (e.Key == Key.Enter) { UnstageSelectedFiles(); e.Handled = true; } };

        SetUpDiffEditor();
        SetUpCommitGraph();

        DataContextChanged += (_, _) => ObserveViewModel();
        ObserveViewModel();
    }

    private void OnWindowVisibilityChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != IsVisibleProperty)
        {
            return;
        }

        if (IsVisible)
        {
            ProcessMemoryTrimService.NotifyActivity("window-shown");
        }
        else
        {
            // Compacting GC + working-set release a couple of seconds after we're dismissed. A
            // quick re-summon fires NotifyActivity and cancels it, so it only runs while idle.
            _ = ProcessMemoryTrimService.RequestIdleTrimAsync("window-hidden", TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>
    /// The graph is an overlay on the commit list's left gutter, so it has to follow the list's
    /// scrolling — otherwise the lanes slide away from the rows they belong to.
    ///
    /// ScrollChanged is a routed event, so it is caught as it bubbles up from the ListBox's inner
    /// ScrollViewer. Looking that ScrollViewer up ahead of time does not work: the History pane
    /// starts collapsed, so at Loaded the template isn't applied and there is nothing to find.
    /// </summary>
    private void SetUpCommitGraph()
    {
        CommitList.AddHandler(ScrollViewer.ScrollChangedEvent, OnCommitListScrolled, RoutingStrategies.Bubble);

        // Right-click doesn't select in a ListBox, but the context menu acts on the selection —
        // so make the commit under the pointer the selected one before the menu opens.
        CommitList.AddHandler(PointerPressedEvent, OnCommitListPointerPressed, RoutingStrategies.Tunnel);

        // Same for the Changes lists, so "Open file" / Discard / Blame target the file under the
        // cursor rather than whatever was left-clicked last (or nothing).
        UnstagedList.AddHandler(PointerPressedEvent, OnFileListPointerPressed, RoutingStrategies.Tunnel);
        StagedList.AddHandler(PointerPressedEvent, OnFileListPointerPressed, RoutingStrategies.Tunnel);

        // ...and the History commit's file list, so its "Open file" targets the right file.
        CommitFilesList.AddHandler(PointerPressedEvent, OnCommitFileListPointerPressed, RoutingStrategies.Tunnel);
    }

    // Enter in the search input. Message already filters live, so this only matters for File: it
    // applies the typed pathspec directly (bypassing the pick list) and closes the dropdown.
    private void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Workspace is not { } workspace)
        {
            return;
        }

        if (workspace.IsFileSearch || workspace.IsContentSearch)
        {
            workspace.ApplySearchCommand.Execute(null);
        }

        SearchDropdownButton.Flyout?.Hide();
        e.Handled = true;
    }

    // Clicking a path in the File pick list applies it and closes the dropdown. The work is deferred
    // off the SelectionChanged event: applying a pick resets the selection and closes the flyout,
    // and doing that mid-event re-enters the ListBox's selection handling.
    private void OnPathSuggestionSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (Workspace is not { } workspace || sender is not ListBox list)
        {
            return;
        }

        if (list.SelectedItem is string path && path.Length > 0)
        {
            Dispatcher.UIThread.Post(() =>
            {
                workspace.PickPath(path);
                list.SelectedItem = null;   // so re-picking the same path fires again
                SearchDropdownButton.Flyout?.Hide();
            });
        }
    }

    // The real row height in DIPs — the ListBoxItem style pins it. The graph's row unit must match.
    private const double CommitRowHeight = 26;

    // "View" on a stash loads its patch into the diff pane, so the flyout must close to reveal it.
    private void OnViewStashClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: StashEntry entry } && Workspace is { } workspace)
        {
            workspace.ViewStashCommand.Execute(entry);
            StashButton.Flyout?.Hide();
        }
    }

    private void OnCommitListScrolled(object? sender, ScrollChangedEventArgs e)
    {
        if (e.Source is not ScrollViewer scroller)
        {
            return;
        }

        GraphView.ScrollOffset = scroller.Offset.Y;

        // Match the graph's row height to the list's ACTUAL one. A nominal 26 drifts: layout rounding
        // snaps each row to a whole device pixel (~26.4 at 125% scale), and over a few hundred commits
        // that lag adds up to a row or two — the bottom dots fall off. Averaging the scroll extent
        // recovers it. But when the list is shorter than the viewport, a resize (e.g. dragging the
        // pane splitter) makes the ScrollViewer briefly report a viewport-sized extent, which would
        // stretch every row and detach the dots — so only trust a value near the real row height.
        if (CommitList.ItemCount > 0 && scroller.Extent.Height > 0)
        {
            var rowHeight = scroller.Extent.Height / CommitList.ItemCount;
            if (rowHeight > CommitRowHeight * 0.75 && rowHeight < CommitRowHeight * 1.5)
            {
                GraphView.RowHeight = rowHeight;
            }
        }
    }

    private void OnCommitListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(CommitList).Properties.IsRightButtonPressed)
        {
            return;
        }

        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>() is { DataContext: CommitInfo commit }
            && Workspace is { } workspace)
        {
            workspace.SelectedCommit = commit;
        }
    }

    /// <summary>
    /// Right-click on a Changes file selects it (so the context menu targets it), unless it's already
    /// part of a multi-selection — then the selection is kept so "Stage selected" still covers them all.
    /// </summary>
    private void OnFileListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ListBox list || !e.GetCurrentPoint(list).Properties.IsRightButtonPressed)
        {
            return;
        }

        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>()?.DataContext is not GitStatusEntry entry)
        {
            return;
        }

        if (!list.SelectedItems!.Contains(entry))
        {
            list.SelectedItems.Clear();
            list.SelectedItems.Add(entry);
        }
    }

    /// <summary>
    /// Right-click selects the History commit-file under the cursor. This list is single-select and bound
    /// to <see cref="WorkspaceViewModel.SelectedCommitFile"/>, so — like the Changes lists — a right-click
    /// must set that selection first, otherwise "Open file" has no target and appears to do nothing.
    /// </summary>
    private void OnCommitFileListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(CommitFilesList).Properties.IsRightButtonPressed)
        {
            return;
        }

        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>()?.DataContext is CommitFileEntry file
            && Workspace is { } ws)
        {
            ws.SelectedCommitFile = file;
        }
    }

    private async void OnCopyShaClick(object? sender, RoutedEventArgs e)
    {
        if (Workspace?.SelectedCommit is { } commit && Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(commit.Sha);
        }
    }

    private async void OnCopySubjectClick(object? sender, RoutedEventArgs e)
    {
        if (Workspace is { SelectedCommit: { } commit } workspace && Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(commit.Subject);
            workspace.StatusText = Loc["Status_CopiedSubject"];
        }
    }

    /// <summary>The Commit column is click-to-copy: it copies the full SHA of the row it sits on.</summary>
    private async void OnCopyShaCellClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: CommitInfo commit } && Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(commit.Sha);

            if (Workspace is { } workspace)
            {
                workspace.StatusText = string.Format(Loc["Status_CopiedSha"], commit.ShortSha);
            }
        }
    }

    /// <summary>
    /// Context-menu "View full message": the history list only carries the subject, so the full
    /// message (subject + body) is fetched on demand and shown in a read-only, copyable popup.
    /// </summary>
    private async void OnViewCommitMessageClick(object? sender, RoutedEventArgs e)
    {
        if (Workspace is not { SelectedCommit: { } commit } workspace)
        {
            return;
        }

        var message = await workspace.GetCommitMessageAsync(commit);
        if (string.IsNullOrWhiteSpace(message))
        {
            message = commit.Subject;   // fall back to what the list already has
        }

        var close = new Button
        {
            Content = Loc["Dialog_Close"],
            MinWidth = 92,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        close.Classes.Add("primary");

        var body = new TextBox
        {
            Text = message,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Cascadia Mono,Consolas,monospace"),
            FontSize = 12,
            MaxHeight = 360,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(body, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);

        var header = new TextBlock
        {
            Text = $"{commit.ShortSha}  ·  {commit.Author}  ·  {commit.WhenDisplay}",
            Opacity = 0.6,
            Margin = new Thickness(0, 0, 0, 10),
        };
        DockPanel.SetDock(header, Dock.Top);

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
            Children = { close },
        };
        DockPanel.SetDock(buttonRow, Dock.Bottom);

        var panel = new DockPanel();
        panel.Children.Add(header);
        panel.Children.Add(buttonRow);
        panel.Children.Add(body);

        var dialog = new Window
        {
            Title = string.Format(Loc["Dialog_CommitMessage_Title"], commit.ShortSha),
            Width = 560,
            SizeToContent = SizeToContent.Height,
            MaxHeight = 560,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Border { Padding = new Thickness(18), Child = panel },
        };

        close.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
    }

    private CommandLogWindow? _commandLogWindow;

    /// <summary>Opens the git command log in its own window (reusing one if it's already open).</summary>
    private void OnShowCommandLogClick(object? sender, RoutedEventArgs e)
    {
        if (Workspace is not { } workspace)
        {
            return;
        }

        workspace.RefreshCommandLog();

        if (_commandLogWindow is null)
        {
            _commandLogWindow = new CommandLogWindow { DataContext = workspace };
            _commandLogWindow.Closed += (_, _) => _commandLogWindow = null;
            _commandLogWindow.Show(this);
        }
        else
        {
            _commandLogWindow.DataContext = workspace;
            _commandLogWindow.Activate();
        }
    }

    private ReflogWindow? _reflogWindow;

    /// <summary>Opens the reflog in its own window (reusing one if it's already open), loading it fresh.</summary>
    private void OnShowReflogClick(object? sender, RoutedEventArgs e)
    {
        if (Workspace is not { } workspace)
        {
            return;
        }

        _ = workspace.LoadReflogAsync();

        if (_reflogWindow is null)
        {
            _reflogWindow = new ReflogWindow { DataContext = workspace };
            _reflogWindow.Closed += (_, _) => _reflogWindow = null;
            _reflogWindow.Show(this);
        }
        else
        {
            _reflogWindow.DataContext = workspace;
            _reflogWindow.Activate();
        }
    }

    private CompareWindow? _compareWindow;

    /// <summary>Branch flyout "Compare with current": current branch (base) vs the selected branch.</summary>
    private void OnCompareBranchClick(object? sender, RoutedEventArgs e)
    {
        if (Workspace is { SelectedBranch: { } branch } ws && !string.IsNullOrEmpty(ws.BranchName))
        {
            ShowCompare(ws.CreateCompare(ws.BranchName, branch.Name));
        }
    }

    /// <summary>Commit context "Compare with…": a picked branch (base) vs the selected commit.</summary>
    private async void OnCompareCommitClick(object? sender, RoutedEventArgs e)
    {
        if (Workspace is not { SelectedCommit: { } commit } ws)
        {
            return;
        }

        var candidates = ws.Branches.Select(b => b.Name).ToList();
        var chosen = await PromptPickAsync(
            candidates,
            Loc["Dialog_PickRef_Title"],
            string.Format(Loc["History_Ctx_CompareWith_Prompt"], commit.ShortSha));

        if (!string.IsNullOrEmpty(chosen))
        {
            ShowCompare(ws.CreateCompare(chosen, commit.Sha));
        }
    }

    /// <summary>Shows (or re-targets) the shared compare window and kicks off its load.</summary>
    private void ShowCompare(CompareViewModel compare)
    {
        if (_compareWindow is null)
        {
            _compareWindow = new CompareWindow { DataContext = compare };
            _compareWindow.Closed += (_, _) => _compareWindow = null;
            _compareWindow.Show(this);
        }
        else
        {
            _compareWindow.DataContext = compare;
            _compareWindow.Activate();
        }

        _ = compare.LoadAsync();
    }

    private BlameWindow? _blameWindow;

    /// <summary>"Blame" on a working-tree file: blames the current working copy (rev=null).</summary>
    private void OnShowBlameClick(object? sender, RoutedEventArgs e)
    {
        if (Workspace is { } ws && (ws.SelectedUnstagedFile ?? ws.SelectedStagedFile) is { } file)
        {
            ShowBlame(ws.CreateBlame(file.Path));
        }
    }

    /// <summary>"Blame" on a file in a history commit: blames it as of that commit.</summary>
    private void OnBlameCommitFileClick(object? sender, RoutedEventArgs e)
    {
        if (Workspace is { SelectedCommitFile: { } file, SelectedCommit: { } commit })
        {
            ShowBlame(Workspace.CreateBlame(file.Path, commit.Sha));
        }
    }

    /// <summary>"File history" on a file in a history commit.</summary>
    private void OnShowCommitFileHistoryClick(object? sender, RoutedEventArgs e)
    {
        if (Workspace is { SelectedCommitFile: { } file } ws)
        {
            _ = ws.ShowFileHistory(file.Path);
        }
    }

    /// <summary>Shows (or re-targets) the shared blame window and kicks off its load.</summary>
    private void ShowBlame(BlameViewModel blame)
    {
        if (_blameWindow is null)
        {
            _blameWindow = new BlameWindow { DataContext = blame };
            _blameWindow.Closed += (_, _) => _blameWindow = null;
            _blameWindow.Show(this);
        }
        else
        {
            _blameWindow.DataContext = blame;
            _blameWindow.Activate();
        }

        _ = blame.LoadAsync();
    }

    private SettingsWindow? _settingsWindow;

    /// <summary>The palette's "update available" banner opens ⚙ Settings → Updates when clicked.</summary>
    private void OnUpdateBannerClick(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.HasUpdateBanner = false;
        }

        OnOpenSettingsClick(sender, e);
    }

    /// <summary>Opens the global Settings window (language + theme) from the palette ⚙.</summary>
    private void OnOpenSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel { Settings: { } settings } mainVm)
        {
            return;
        }

        // App wires a shared UpdateService; fall back to a fresh one so settings still opens if it didn't.
        var updater = mainVm.UpdateService
            ?? new Services.Updates.UpdateService(Services.Updates.AppVersionInfo.CurrentVersion);

        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow
            {
                DataContext = new SettingsViewModel(settings, updater, mainVm.Hotkeys),
            };
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show(this);
        }
        else
        {
            _settingsWindow.Activate();
        }
    }

    // History column resize: a grip sits on each internal column boundary and trades width between
    // the two columns it separates, so every column (Message included, since it's the flexible one
    // the Message|Author grip borrows from) is resizable. The header and every row bind the shared
    // widths. Done by hand (not a GridSplitter) so it can't fight the binding or the sort buttons.
    private const double MinColumnWidth = 50;
    private string? _resizingBoundary;
    private double _resizeStartPointerX;
    private double _startAuthor;
    private double _startDate;
    private double _startCommit;

    private void OnColumnGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: string boundary } grip || Workspace is not { } ws)
        {
            return;
        }

        _resizingBoundary = boundary;
        _resizeStartPointerX = e.GetPosition(this).X;
        _startAuthor = ws.AuthorColumnWidth.Value;
        _startDate = ws.DateColumnWidth.Value;
        _startCommit = ws.CommitColumnWidth.Value;

        e.Pointer.Capture(grip);
        e.Handled = true;
    }

    private void OnColumnGripMoved(object? sender, PointerEventArgs e)
    {
        if (_resizingBoundary is null || Workspace is not { } ws)
        {
            return;
        }

        var d = e.GetPosition(this).X - _resizeStartPointerX;

        switch (_resizingBoundary)
        {
            case "MsgAuthor":   // Message is the flexible column, so it just absorbs the change.
                ws.AuthorColumnWidth = new GridLength(Math.Max(MinColumnWidth, _startAuthor - d));
                break;

            case "AuthorDate":  // trade between the two fixed neighbours, keeping their sum constant
                d = Math.Clamp(d, MinColumnWidth - _startAuthor, _startDate - MinColumnWidth);
                ws.AuthorColumnWidth = new GridLength(_startAuthor + d);
                ws.DateColumnWidth = new GridLength(_startDate - d);
                break;

            case "DateCommit":
                d = Math.Clamp(d, MinColumnWidth - _startDate, _startCommit - MinColumnWidth);
                ws.DateColumnWidth = new GridLength(_startDate + d);
                ws.CommitColumnWidth = new GridLength(_startCommit - d);
                break;
        }
    }

    private void OnColumnGripReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_resizingBoundary is not null)
        {
            e.Pointer.Capture(null);
            _resizingBoundary = null;
            e.Handled = true;
        }
    }

    // Graph divider: drag to set how wide the lane-graph gutter is (clips the graph when narrowed),
    // freeing width for the message columns. Mirrors the column grips.
    private bool _graphResizing;
    private double _graphResizeStartX;
    private double _graphResizeStartGutter;

    private void OnGraphGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border grip || Workspace is not { } ws)
        {
            return;
        }

        _graphResizing = true;
        _graphResizeStartX = e.GetPosition(this).X;
        _graphResizeStartGutter = ws.GraphGutter;
        e.Pointer.Capture(grip);
        e.Handled = true;
    }

    private void OnGraphGripMoved(object? sender, PointerEventArgs e)
    {
        if (!_graphResizing || Workspace is not { } ws)
        {
            return;
        }

        ws.SetGraphGutter(_graphResizeStartGutter + (e.GetPosition(this).X - _graphResizeStartX));
    }

    private void OnGraphGripReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_graphResizing)
        {
            e.Pointer.Capture(null);
            _graphResizing = false;
            e.Handled = true;
        }
    }

    // History opens with the diff pane hidden so the commit list (and its graph) get the full width;
    // picking a commit reveals it, and the ✕ in the commit-files pane collapses it again.
    private bool _historyDiffShown;

    private void OnHideDiffPaneClick(object? sender, RoutedEventArgs e)
    {
        _historyDiffShown = false;
        SyncHistoryDiff();
    }

    /// <summary>
    /// Shows/hides the right-hand diff pane. In History it starts hidden (so the list spans the full
    /// width); a selected commit reveals it, the ✕ hides it. Changes mode always shows it.
    /// </summary>
    private void SyncHistoryDiff()
    {
        if (HistoryPane is null || MainSplitter is null || DiffPaneGrid is null)
        {
            return;
        }

        var showDiff = _observedWorkspace?.IsHistoryMode != true || _historyDiffShown;

        MainSplitter.IsVisible = showDiff;
        DiffPaneGrid.IsVisible = showDiff;
        Grid.SetColumnSpan(HistoryPane, showDiff ? 1 : 3);
    }

    // Graph dot interaction: hovering a commit's lane shows its branch/HEAD popup; clicking selects it.
    private void OnGraphPointerMoved(object? sender, PointerEventArgs e)
    {
        if (CommitAtGraphY(sender, e) is { } commit && Workspace is { } ws)
        {
            ws.ShowCommitHoverInfo(commit);
        }
    }

    private void OnGraphPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (CommitAtGraphY(sender, e) is { } commit && Workspace is { } ws)
        {
            ws.SelectedCommit = commit;
        }
    }

    // The commit whose row is under the pointer — found by the actual realized item bounds (in the
    // list's own coordinates), so it stays correct regardless of scrolling.
    private CommitInfo? CommitAtGraphY(object? sender, PointerEventArgs e)
    {
        var y = e.GetPosition(CommitList).Y;

        foreach (var container in CommitList.GetRealizedContainers())
        {
            if (container.DataContext is not CommitInfo commit)
            {
                continue;
            }

            var top = container.TranslatePoint(default, CommitList)?.Y;
            if (top is { } t && y >= t && y < t + container.Bounds.Height)
            {
                return commit;
            }
        }

        return null;
    }

    private WorkspaceViewModel? Workspace => (DataContext as MainViewModel)?.Workspace;

    /// <summary>Double-clicking a branch badge in the graph checks it out, à la Git Graph.</summary>
    private void OnRefBadgeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: GitRef reference } && Workspace is { } ws)
        {
            _ = ws.CheckoutRef(reference);
            e.Handled = true;   // don't let it bubble up to the row
        }
    }

    // The right-click menu on a branch badge. Its DataContext is the GitRef the badge sits on.
    private void OnCheckoutRefClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: GitRef reference } && Workspace is { } ws)
        {
            _ = ws.CheckoutRef(reference);
        }
    }

    private void OnDeleteBranchClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: GitRef reference } && Workspace is { } ws)
        {
            _ = ws.DeleteRef(reference);
        }
    }

    private void OnOpenRefOnRemoteClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: GitRef reference } && Workspace is { } ws)
        {
            ws.OpenRefOnRemoteCommand.Execute(reference);
        }
    }

    private void OnOpenFileOnRemoteClick(object? sender, RoutedEventArgs e)
    {
        if (Workspace is { } ws && (ws.SelectedUnstagedFile ?? ws.SelectedStagedFile) is { } file)
        {
            ws.OpenFileOnRemoteCommand.Execute(file.Path);
        }
    }

    /// <summary>Context-menu "Open file" on the Changes lists: opens the working copy.</summary>
    private void OnOpenFileClick(object? sender, RoutedEventArgs e)
    {
        if (Workspace is { } ws && (ws.SelectedUnstagedFile ?? ws.SelectedStagedFile) is { } file)
        {
            OpenWorkingFile(ws, file.Path);
        }
    }

    /// <summary>Context-menu "Open file" on a History commit's file list: opens the working copy.</summary>
    private void OnOpenCommitFileClick(object? sender, RoutedEventArgs e)
    {
        if (Workspace is { SelectedCommitFile: { } file } ws)
        {
            OpenWorkingFile(ws, file.Path);
        }
    }

    /// <summary>
    /// Opens the working-tree copy of a repo-relative path. Tries the default application first; if the
    /// file type has no association (ShellExecute would otherwise just throw), falls back to the OS
    /// "Open with…" chooser so the click always does something visible.
    /// </summary>
    private static void OpenWorkingFile(WorkspaceViewModel ws, string relativePath)
    {
        // GetFullPath also normalises the forward slashes git uses into the platform separator.
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(ws.Repository.Path, relativePath));

        if (!System.IO.File.Exists(full))
        {
            ws.StatusText = string.Format(Loc["Status_FileNotFound"], relativePath);
            return;
        }

        if (TryShellOpen(full, verb: null) || TryShellOpen(full, verb: "openas"))
        {
            return;
        }

        ws.StatusText = string.Format(Loc["Status_OpenFileFailed"], relativePath);

        static bool TryShellOpen(string path, string? verb)
        {
            try
            {
                var info = new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true };
                if (verb is not null)
                {
                    info.Verb = verb;
                }

                System.Diagnostics.Process.Start(info);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

private void OnShowFileHistoryClick(object? sender, RoutedEventArgs e)
    {
        if (Workspace is { } ws && (ws.SelectedUnstagedFile ?? ws.SelectedStagedFile) is { } file)
        {
            _ = ws.ShowFileHistory(file.Path);
        }
    }

    private void OnStageSelectedClick(object? sender, RoutedEventArgs e) => StageSelectedFiles();

    private void OnUnstageSelectedClick(object? sender, RoutedEventArgs e) => UnstageSelectedFiles();

    private void StageSelectedFiles()
    {
        if (Workspace is { } ws)
        {
            _ = ws.StageFiles(SelectedEntries(UnstagedList));
        }
    }

    private void UnstageSelectedFiles()
    {
        if (Workspace is { } ws)
        {
            _ = ws.UnstageFiles(SelectedEntries(StagedList));
        }
    }

    private void OnDiscardSelectedClick(object? sender, RoutedEventArgs e)
    {
        if (Workspace is { } ws)
        {
            _ = ws.DiscardFiles(SelectedEntries(UnstagedList));
        }
    }

    private static IReadOnlyList<GitStatusEntry> SelectedEntries(ListBox list) =>
        list.SelectedItems?.Cast<GitStatusEntry>().ToList() ?? [];

    /// <summary>
    /// TextMate does the syntax colouring (spec §1: don't hand-roll a highlighter); the
    /// background renderer tints whole +/- lines on top of it.
    /// </summary>
    private void SetUpDiffEditor() => DiffEditorSetup.Apply(DiffEditor);

    // AvaloniaEdit's document isn't a good binding target, so the diff text is pushed into the
    // editor whenever the workspace reports a new one.
    private void ObserveViewModel()
    {
        if (_observedMain is not null)
        {
            _observedMain.PropertyChanged -= OnMainPropertyChanged;
        }

        _observedMain = DataContext as MainViewModel;

        if (_observedMain is not null)
        {
            _observedMain.PropertyChanged += OnMainPropertyChanged;
        }

        ObserveWorkspace();
    }

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Workspace))
        {
            ObserveWorkspace();
        }
    }

    private void ObserveWorkspace()
    {
        if (_observedWorkspace is not null)
        {
            _observedWorkspace.PropertyChanged -= OnWorkspacePropertyChanged;
        }

        _observedWorkspace = Workspace;

        if (_observedWorkspace is not null)
        {
            _observedWorkspace.PropertyChanged += OnWorkspacePropertyChanged;
            _observedWorkspace.ConfirmDirtyCheckout = ConfirmDirtyCheckoutAsync;
            _observedWorkspace.ConfirmDeleteBranch = ConfirmDeleteBranchAsync;
            _observedWorkspace.ConfirmDiscardAll = ConfirmDiscardAllAsync;
            _observedWorkspace.ConfirmDiscardFiles = ConfirmDiscardFilesAsync;
            _observedWorkspace.PromptPullSource = PromptPullSourceAsync;
            _observedWorkspace.PromptPushTarget = PromptPushTargetAsync;
            _observedWorkspace.OpenUrlInBrowser = BrowserLauncher.Open;
            _observedWorkspace.SetClipboardText = text => Clipboard?.SetTextAsync(text) ?? Task.CompletedTask;
            _observedWorkspace.PromptPickRef = (items, prompt) => PromptPickAsync(items, Loc["Dialog_PickRef_Title"], prompt);
            _observedWorkspace.PromptTagName = () => PromptNameAsync(Loc["Dialog_AddTag_Title"], Loc["Dialog_TagName_Placeholder"]);
            _observedWorkspace.PromptBranchName = () => PromptNameAsync(Loc["Dialog_CreateBranch_Title"], Loc["Branch_NewNamePlaceholder"]);
            _observedWorkspace.PromptResetMode = PromptResetModeAsync;
            _observedWorkspace.ConfirmRebase = ConfirmRebaseAsync;
        }

        UpdateTitle();
        UpdateDiffEditor();
        SyncCommitFilesRow();
        SyncHistoryDiff();
    }

    /// <summary>
    /// Carries the open repo and its current branch in the OS title bar, so the toolbar row no
    /// longer has to. The palette (no repo open) shows just the app name.
    /// </summary>
    private void UpdateTitle()
    {
        if (Workspace is not { } workspace)
        {
            Title = "GitFlick";
            return;
        }

        var branch = string.IsNullOrEmpty(workspace.BranchName)
            ? string.Empty
            : $"  ·  {workspace.BranchName}";
        Title = $"{workspace.Repository.Name}{branch}  —  GitFlick";
    }

    /// <summary>
    /// Confirms deleting a branch. Returns null to cancel, else whether to force-delete (the
    /// checkbox), so an unmerged branch isn't dropped without the user opting in.
    /// </summary>
    private async Task<bool?> ConfirmDeleteBranchAsync(string branch)
    {
        var force = new CheckBox { Content = Loc["Dialog_ForceDelete"] };
        var cancel = new Button { Content = Loc["Dialog_Cancel"], MinWidth = 92, HorizontalContentAlignment = HorizontalAlignment.Center };
        var delete = new Button { Content = Loc["Dialog_Delete"], MinWidth = 92, HorizontalContentAlignment = HorizontalAlignment.Center };
        delete.Classes.Add("danger");

        var dialog = new Window
        {
            Title = Loc["Dialog_DeleteBranch_Title"],
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Border
            {
                Padding = new Thickness(22),
                Child = new StackPanel
                {
                    Spacing = 16,
                    MaxWidth = 380,
                    Children =
                    {
                        new TextBlock
                        {
                            TextWrapping = TextWrapping.Wrap,
                            Text = string.Format(Loc["Dialog_DeleteBranch_Body"], branch),
                        },
                        force,
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 8,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Children = { cancel, delete },
                        },
                    },
                },
            },
        };

        cancel.Click += (_, _) => dialog.Close(null);
        delete.Click += (_, _) => dialog.Close((bool?)(force.IsChecked == true));

        return await dialog.ShowDialog<bool?>(this);
    }

    /// <summary>
    /// Confirms discarding every change. Returns null to cancel, otherwise whether to also delete
    /// untracked files (the checkbox). Destructive, so the affirmative is a red Discard button.
    /// </summary>
    private async Task<bool?> ConfirmDiscardAllAsync()
    {
        var untracked = new CheckBox { Content = Loc["Dialog_AlsoDeleteUntracked"] };
        var cancel = new Button { Content = Loc["Dialog_Cancel"], MinWidth = 92, HorizontalContentAlignment = HorizontalAlignment.Center };
        var discard = new Button { Content = Loc["Dialog_Discard"], MinWidth = 92, HorizontalContentAlignment = HorizontalAlignment.Center };
        discard.Classes.Add("danger");

        var dialog = new Window
        {
            Title = Loc["Dialog_DiscardAll_Title"],
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Border
            {
                Padding = new Thickness(22),
                Child = new StackPanel
                {
                    Spacing = 16,
                    MaxWidth = 380,
                    Children =
                    {
                        new TextBlock
                        {
                            TextWrapping = TextWrapping.Wrap,
                            Text = Loc["Dialog_DiscardAll_Body"],
                        },
                        untracked,
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 8,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Children = { cancel, discard },
                        },
                    },
                },
            },
        };

        cancel.Click += (_, _) => dialog.Close(null);
        discard.Click += (_, _) => dialog.Close((bool?)(untracked.IsChecked == true));

        return await dialog.ShowDialog<bool?>(this);
    }

    /// <summary>
    /// Confirms discarding specific files' changes (destructive → red Discard). Returns whether to
    /// proceed.
    /// </summary>
    private async Task<bool> ConfirmDiscardFilesAsync(IReadOnlyList<string> paths)
    {
        var cancel = new Button { Content = Loc["Dialog_Cancel"], MinWidth = 92, HorizontalContentAlignment = HorizontalAlignment.Center };
        var discard = new Button { Content = Loc["Dialog_Discard"], MinWidth = 92, HorizontalContentAlignment = HorizontalAlignment.Center };
        discard.Classes.Add("danger");

        var target = paths.Count == 1 ? $"“{paths[0]}”" : string.Format(Loc["Dialog_DiscardChanges_MultipleTarget"], paths.Count);

        var dialog = new Window
        {
            Title = Loc["Dialog_DiscardChanges_Title"],
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Border
            {
                Padding = new Thickness(22),
                Child = new StackPanel
                {
                    Spacing = 16,
                    MaxWidth = 380,
                    Children =
                    {
                        new TextBlock
                        {
                            TextWrapping = TextWrapping.Wrap,
                            Text = string.Format(Loc["Dialog_DiscardChanges_Body"], target),
                        },
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 8,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Children = { cancel, discard },
                        },
                    },
                },
            },
        };

        cancel.Click += (_, _) => dialog.Close(false);
        discard.Click += (_, _) => dialog.Close(true);

        return await dialog.ShowDialog<bool>(this);
    }

    /// <summary>
    /// "Push to…": picks which remote to push the current branch to. Returns the remote name, or
    /// null to cancel.
    /// </summary>
    private async Task<string?> PromptPushTargetAsync(IReadOnlyList<string> remotes, string branch)
    {
        var combo = new ComboBox
        {
            ItemsSource = remotes,
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var cancel = new Button { Content = Loc["Dialog_Cancel"], MinWidth = 92, HorizontalContentAlignment = HorizontalAlignment.Center };
        var push = new Button { Content = Loc["Dialog_Push"], MinWidth = 92, HorizontalContentAlignment = HorizontalAlignment.Center };
        push.Classes.Add("primary");

        var dialog = new Window
        {
            Title = Loc["Dialog_PushTo_Title"],
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Border
            {
                Padding = new Thickness(22),
                Child = new StackPanel
                {
                    Spacing = 14,
                    MinWidth = 320,
                    Children =
                    {
                        new TextBlock
                        {
                            TextWrapping = TextWrapping.Wrap,
                            Text = string.Format(Loc["Dialog_PushTo_Body"], branch),
                        },
                        combo,
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 8,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Children = { cancel, push },
                        },
                    },
                },
            },
        };

        cancel.Click += (_, _) => dialog.Close(null);
        push.Click += (_, _) => dialog.Close(combo.SelectedItem as string);

        return await dialog.ShowDialog<string?>(this);
    }

    /// <summary>Generic "pick one item from a list" dialog (set-upstream, compare). Returns null to cancel.</summary>
    private async Task<string?> PromptPickAsync(IReadOnlyList<string> items, string title, string body)
    {
        if (items.Count == 0)
        {
            return null;
        }

        var combo = new ComboBox
        {
            ItemsSource = items,
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var cancel = new Button { Content = Loc["Dialog_Cancel"], MinWidth = 92, HorizontalContentAlignment = HorizontalAlignment.Center };
        var ok = new Button { Content = Loc["Dialog_OK"], MinWidth = 92, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Classes.Add("primary");

        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Border
            {
                Padding = new Thickness(22),
                Child = new StackPanel
                {
                    Spacing = 14,
                    MinWidth = 340,
                    Children =
                    {
                        new TextBlock { TextWrapping = TextWrapping.Wrap, Text = body },
                        combo,
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 8,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Children = { cancel, ok },
                        },
                    },
                },
            },
        };

        cancel.Click += (_, _) => dialog.Close(null);
        ok.Click += (_, _) => dialog.Close(combo.SelectedItem as string);

        return await dialog.ShowDialog<string?>(this);
    }

    /// <summary>
    /// "Pull from…": picks a remote and the branch on it to pull into the current branch. Returns
    /// null to cancel (or if either field is left empty).
    /// </summary>
    private async Task<WorkspaceViewModel.RemoteBranch?> PromptPullSourceAsync(WorkspaceViewModel.PullSourceOptions options)
    {
        var branch = options.CurrentBranch;

        var remoteCombo = new ComboBox
        {
            ItemsSource = options.Remotes,
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        // Fuzzy completion over the branches that actually exist on the chosen remote, so a repo with
        // dozens of them is typeable ("cmd" finds "claude/md-docs"). Free text still wins: whatever is
        // typed is what gets pulled, even if it matches nothing in the list.
        var branchBox = new AutoCompleteBox
        {
            Text = branch,
            PlaceholderText = Loc["Dialog_PullBranchPlaceholder"],
            FilterMode = AutoCompleteFilterMode.Custom,
            ItemFilter = (search, item) =>
                string.IsNullOrEmpty(search)
                || (item is string name && FuzzyMatcher.TryMatch(name, search, out _)),
            MinimumPrefixLength = 0,
            MaxDropDownHeight = 220,
        };

        // The candidate list belongs to the selected remote, so refill it whenever that changes.
        void LoadBranches()
        {
            if (remoteCombo.SelectedItem is string remote)
            {
                branchBox.ItemsSource = options.BranchesOn(remote);
            }
        }

        remoteCombo.SelectionChanged += (_, _) => LoadBranches();
        LoadBranches();
        var cancel = new Button { Content = Loc["Dialog_Cancel"], MinWidth = 92, HorizontalContentAlignment = HorizontalAlignment.Center };
        var pull = new Button { Content = Loc["Dialog_Pull"], MinWidth = 92, HorizontalContentAlignment = HorizontalAlignment.Center };
        pull.Classes.Add("primary");

        var dialog = new Window
        {
            Title = Loc["Dialog_PullFrom_Title"],
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Border
            {
                Padding = new Thickness(22),
                Child = new StackPanel
                {
                    Spacing = 10,
                    MinWidth = 320,
                    Children =
                    {
                        new TextBlock
                        {
                            TextWrapping = TextWrapping.Wrap,
                            Margin = new Thickness(0, 0, 0, 4),
                            Text = string.Format(Loc["Dialog_PullFrom_Body"], branch),
                        },
                        new TextBlock { Text = Loc["Dialog_Remote"], FontSize = 10, Opacity = 0.5 },
                        remoteCombo,
                        new TextBlock { Text = Loc["Dialog_Branch"], FontSize = 10, Opacity = 0.5 },
                        branchBox,
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 8,
                            Margin = new Thickness(0, 6, 0, 0),
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Children = { cancel, pull },
                        },
                    },
                },
            },
        };

        cancel.Click += (_, _) => dialog.Close(null);
        pull.Click += (_, _) =>
        {
            var remote = remoteCombo.SelectedItem as string;
            var wanted = branchBox.Text?.Trim() ?? string.Empty;
            dialog.Close(remote is { Length: > 0 } && wanted.Length > 0
                ? new WorkspaceViewModel.RemoteBranch(remote, wanted)
                : null);
        };

        return await dialog.ShowDialog<WorkspaceViewModel.RemoteBranch?>(this);
    }

    /// <summary>
    /// The branch-switch safety net: with uncommitted changes, ask before checking out. Shown for
    /// the Branch flyout, the commit context menu, and double-clicking a branch badge.
    /// </summary>
    private async Task<bool> ConfirmDirtyCheckoutAsync(string target)
    {
        var cancel = new Button { Content = Loc["Dialog_Cancel"], MinWidth = 92, HorizontalContentAlignment = HorizontalAlignment.Center };
        var proceed = new Button { Content = Loc["Dialog_SwitchAnyway"], MinWidth = 92, HorizontalContentAlignment = HorizontalAlignment.Center };
        proceed.Classes.Add("primary");

        var dialog = new Window
        {
            Title = Loc["Dialog_DirtyCheckout_Title"],
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Border
            {
                Padding = new Thickness(22),
                Child = new StackPanel
                {
                    Spacing = 18,
                    MaxWidth = 380,
                    Children =
                    {
                        new TextBlock
                        {
                            TextWrapping = TextWrapping.Wrap,
                            Text = string.Format(Loc["Dialog_DirtyCheckout_Body"], target),
                        },
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 8,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Children = { cancel, proceed },
                        },
                    },
                },
            },
        };

        cancel.Click += (_, _) => dialog.Close(false);
        proceed.Click += (_, _) => dialog.Close(true);

        return await dialog.ShowDialog<bool>(this);
    }

    /// <summary>Shared name prompt for "Add tag" and "Create branch". Returns the trimmed name, or null.</summary>
    private async Task<string?> PromptNameAsync(string title, string watermark)
    {
        var input = new TextBox
        {
            PlaceholderText = watermark,
            MinWidth = 320,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        var cancel = new Button { Content = Loc["Dialog_Cancel"], MinWidth = 92, HorizontalContentAlignment = HorizontalAlignment.Center };
        var create = new Button { Content = Loc["Branch_Create"], MinWidth = 92, HorizontalContentAlignment = HorizontalAlignment.Center };
        create.Classes.Add("primary");

        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Border
            {
                Padding = new Thickness(22),
                Child = new StackPanel
                {
                    Spacing = 12,
                    MinWidth = 320,
                    Children =
                    {
                        input,
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 8,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Children = { cancel, create },
                        },
                    },
                },
            },
        };

        string? Result() => string.IsNullOrWhiteSpace(input.Text) ? null : input.Text!.Trim();
        cancel.Click += (_, _) => dialog.Close(null);
        create.Click += (_, _) => dialog.Close(Result());
        input.KeyDown += (_, ev) =>
        {
            if (ev.Key == Key.Enter)
            {
                dialog.Close(Result());
            }
        };

        return await dialog.ShowDialog<string?>(this);
    }

    /// <summary>Picks the reset mode (soft/mixed/hard), warning before a destructive hard reset. Null cancels.</summary>
    private async Task<GitResetMode?> PromptResetModeAsync(string shortSha)
    {
        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, SelectedIndex = 1 };
        combo.Items.Add(new ComboBoxItem { Content = Loc["Dialog_Reset_Soft"] });
        combo.Items.Add(new ComboBoxItem { Content = Loc["Dialog_Reset_Mixed"] });
        combo.Items.Add(new ComboBoxItem { Content = Loc["Dialog_Reset_Hard"] });

        var warning = new TextBlock
        {
            Text = Loc["Dialog_Reset_HardWarning"],
            Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x53, 0x4B)),
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };
        combo.SelectionChanged += (_, _) => warning.IsVisible = combo.SelectedIndex == 2;

        var cancel = new Button { Content = Loc["Dialog_Cancel"], MinWidth = 92, HorizontalContentAlignment = HorizontalAlignment.Center };
        var reset = new Button { Content = Loc["Dialog_Reset_Confirm"], MinWidth = 92, HorizontalContentAlignment = HorizontalAlignment.Center };
        reset.Classes.Add("primary");

        var dialog = new Window
        {
            Title = Loc["Dialog_Reset_Title"],
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Border
            {
                Padding = new Thickness(22),
                Child = new StackPanel
                {
                    Spacing = 12,
                    MaxWidth = 380,
                    Children =
                    {
                        new TextBlock { Text = string.Format(Loc["Dialog_Reset_Body"], shortSha), TextWrapping = TextWrapping.Wrap },
                        combo,
                        warning,
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 8,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Children = { cancel, reset },
                        },
                    },
                },
            },
        };

        cancel.Click += (_, _) => dialog.Close(null);
        reset.Click += (_, _) => dialog.Close(combo.SelectedIndex switch
        {
            0 => GitResetMode.Soft,
            2 => GitResetMode.Hard,
            _ => GitResetMode.Mixed,
        });

        return await dialog.ShowDialog<GitResetMode?>(this);
    }

    /// <summary>Confirms a rebase (it rewrites history). Returns true to proceed.</summary>
    private async Task<bool> ConfirmRebaseAsync(string shortSha)
    {
        var cancel = new Button { Content = Loc["Dialog_Cancel"], MinWidth = 92, HorizontalContentAlignment = HorizontalAlignment.Center };
        var proceed = new Button { Content = Loc["Dialog_Rebase_Confirm"], MinWidth = 92, HorizontalContentAlignment = HorizontalAlignment.Center };
        proceed.Classes.Add("primary");

        var dialog = new Window
        {
            Title = Loc["Dialog_Rebase_Title"],
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Border
            {
                Padding = new Thickness(22),
                Child = new StackPanel
                {
                    Spacing = 16,
                    MaxWidth = 380,
                    Children =
                    {
                        new TextBlock { Text = string.Format(Loc["Dialog_Rebase_Body"], shortSha), TextWrapping = TextWrapping.Wrap },
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 8,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Children = { cancel, proceed },
                        },
                    },
                },
            },
        };

        cancel.Click += (_, _) => dialog.Close(false);
        proceed.Click += (_, _) => dialog.Close(true);

        return await dialog.ShowDialog<bool>(this);
    }

    private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceViewModel.DiffText))
        {
            UpdateDiffEditor();
        }
        else if (e.PropertyName == nameof(WorkspaceViewModel.BranchName))
        {
            UpdateTitle();   // e.g. after a checkout
        }
        else if (e.PropertyName == nameof(WorkspaceViewModel.IsHistoryMode))
        {
            _historyDiffShown = false;   // History opens with the diff hidden (full-width commit list)
            SyncCommitFilesRow();
            SyncHistoryDiff();
        }
        // A single click only selects (and shows the branch popup); the diff pane opens on double-click
        // via OnCommitActivated, so browsing the list stays light.
    }

    /// <summary>Double-clicking a commit (row or graph dot) opens the diff pane for it.</summary>
    private void OnCommitActivated(object? sender, TappedEventArgs e)
    {
        if (_observedWorkspace?.SelectedCommit is not null)
        {
            _historyDiffShown = true;
            SyncHistoryDiff();
        }
    }

    private void UpdateDiffEditor() => DiffEditor.Text = _observedWorkspace?.DiffText ?? string.Empty;

    /// <summary>
    /// The commit-files list (with its draggable divider) exists only in History mode. Collapse its row
    /// to 0 in Changes mode so the diff gets the full height, and restore the last dragged height when
    /// History comes back. A GridSplitter writes the row height directly, so this can't be a binding.
    /// </summary>
    private void SyncCommitFilesRow()
    {
        if (DiffPaneGrid is null)
        {
            return;
        }

        var commitFilesRow = DiffPaneGrid.RowDefinitions[0];

        if (_observedWorkspace?.IsHistoryMode == true)
        {
            commitFilesRow.Height = _commitFilesHeight;
        }
        else
        {
            if (commitFilesRow.Height.IsAbsolute && commitFilesRow.Height.Value > 0)
            {
                _commitFilesHeight = commitFilesRow.Height;   // remember what the user dragged it to
            }

            commitFilesRow.Height = new GridLength(0);
        }
    }

    private void OnBackClick(object? sender, RoutedEventArgs e) => ReturnToPalette();

    /// <summary>Leaves the workspace and returns focus to the palette search box.</summary>
    private void ReturnToPalette()
    {
        if (DataContext is MainViewModel vm && vm.IsRepoOpen)
        {
            vm.CloseRepo();
            FocusInput();
        }
    }

    /// <summary>
    /// Puts the caret where the user expects it the instant the window is summoned.
    /// Call after the window is shown and activated.
    /// </summary>
    public void FocusInput()
    {
        // With a repo open the palette isn't on screen; don't pull focus onto a hidden search box.
        if (DataContext is MainViewModel { IsRepoOpen: true })
        {
            return;
        }

        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        // Escape always steps back: workspace -> palette, palette -> hidden.
        if (e.Key == Key.Escape)
        {
            if (vm.IsRepoOpen)
            {
                ReturnToPalette();
            }
            else
            {
                Hide();
            }

            e.Handled = true;
            return;
        }

        // Everything below is palette-only; workspace keystrokes (commit box, etc.) pass through.
        if (!vm.IsPaletteVisible)
        {
            return;
        }

        if (e.KeyModifiers == KeyModifiers.Control)
        {
            switch (e.Key)
            {
                case Key.O:
                    _ = PinRepositoryAsync();
                    e.Handled = true;
                    return;

                case Key.D:
                    vm.RemoveSelected();
                    e.Handled = true;
                    return;
            }
        }

        switch (e.Key)
        {
            case Key.Down:
                vm.MoveSelection(1);
                ScrollSelectionIntoView(vm);
                e.Handled = true;
                break;

            case Key.Up:
                vm.MoveSelection(-1);
                ScrollSelectionIntoView(vm);
                e.Handled = true;
                break;

            case Key.Enter:
                vm.OpenSelected();
                e.Handled = true;
                break;
        }
    }

    private async Task PinRepositoryAsync()
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Loc["Dialog_PinRepo_Title"],
            AllowMultiple = false,
        });

        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
        {
            vm.AddRepository(path);
        }

        FocusInput();
    }

    private async void OnOpenRepositoryClick(object? sender, RoutedEventArgs e) => await OpenRepositoryAsync();

    /// <summary>
    /// The "Open" button: pick a folder and drop straight into its workspace. Unlike Ctrl+O
    /// (which only pins), this opens the repository once it's added.
    /// </summary>
    private async Task OpenRepositoryAsync()
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Loc["Dialog_OpenRepo_Title"],
            AllowMultiple = false,
        });

        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
        {
            vm.OpenRepository(path);
        }

        // A rejected pick (non-repo) or a cancel leaves the palette up — put the caret back.
        if (vm.IsPaletteVisible)
        {
            FocusInput();
        }
    }

    private void ScrollSelectionIntoView(MainViewModel vm)
    {
        if (vm.SelectedRepo is { } selected)
        {
            RepoList.ScrollIntoView(selected);
        }
    }
}
