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
    }

    private void OnCommitListScrolled(object? sender, ScrollChangedEventArgs e)
    {
        if (e.Source is ScrollViewer scroller)
        {
            GraphView.ScrollOffset = scroller.Offset.Y;

            // Match the graph's row height to the list's ACTUAL one. A nominal 26 drifts: layout
            // rounding snaps each row to a whole device pixel (~26.4 at 125% scale), and over a
            // few hundred commits that lag adds up to a row or two — the bottom dots fall off.
            if (CommitList.ItemCount > 0 && scroller.Extent.Height > 0)
            {
                GraphView.RowHeight = scroller.Extent.Height / CommitList.ItemCount;
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

    private async void OnCopyShaClick(object? sender, RoutedEventArgs e)
    {
        if (Workspace?.SelectedCommit is { } commit && Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(commit.Sha);
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
                workspace.StatusText = $"Copied {commit.ShortSha} to clipboard";
            }
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

    private static IReadOnlyList<GitStatusEntry> SelectedEntries(ListBox list) =>
        list.SelectedItems?.Cast<GitStatusEntry>().ToList() ?? [];

    /// <summary>
    /// TextMate does the syntax colouring (spec §1: don't hand-roll a highlighter); the
    /// background renderer tints whole +/- lines on top of it.
    /// </summary>
    private void SetUpDiffEditor()
    {
        DiffEditor.TextArea.TextView.BackgroundRenderers.Add(new DiffLineBackgroundRenderer());

        var registryOptions = new RegistryOptions(ThemeName.DarkPlus);
        var installation = DiffEditor.InstallTextMate(registryOptions);
        installation.SetGrammar(registryOptions.GetScopeByLanguageId("diff"));
    }

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
        }

        UpdateDiffEditor();
    }

    /// <summary>
    /// The branch-switch safety net: with uncommitted changes, ask before checking out. Shown for
    /// the Branch flyout, the commit context menu, and double-clicking a branch badge.
    /// </summary>
    private async Task<bool> ConfirmDirtyCheckoutAsync(string target)
    {
        var cancel = new Button { Content = "Cancel", MinWidth = 92, HorizontalContentAlignment = HorizontalAlignment.Center };
        var proceed = new Button { Content = "Switch anyway", MinWidth = 92, HorizontalContentAlignment = HorizontalAlignment.Center };
        proceed.Classes.Add("primary");

        var dialog = new Window
        {
            Title = "Uncommitted changes",
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
                            Text = $"You have uncommitted changes.\n\nSwitch to “{target}” anyway? " +
                                   "Git keeps your changes if it can, and refuses the switch if any would be overwritten.",
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

    private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceViewModel.DiffText))
        {
            UpdateDiffEditor();
        }
    }

    private void UpdateDiffEditor() => DiffEditor.Text = _observedWorkspace?.DiffText ?? string.Empty;

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
            Title = "Pin a Git repository",
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
            Title = "Open a Git repository",
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
