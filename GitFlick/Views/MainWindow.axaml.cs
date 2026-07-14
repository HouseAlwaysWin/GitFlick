using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using AvaloniaEdit.TextMate;
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

        // Mouse users expect a double-click to open a repo / move a file across the staging line.
        RepoList.DoubleTapped += (_, _) => (DataContext as MainViewModel)?.OpenSelected();
        UnstagedList.DoubleTapped += (_, _) => Workspace?.StageCommand.Execute(Workspace.SelectedUnstagedFile);
        StagedList.DoubleTapped += (_, _) => Workspace?.UnstageCommand.Execute(Workspace.SelectedStagedFile);

        SetUpDiffEditor();
        SetUpCommitGraph();

        DataContextChanged += (_, _) => ObserveViewModel();
        ObserveViewModel();
    }

    /// <summary>
    /// The graph is an overlay on the commit list's left gutter, so it has to follow the list's
    /// scrolling — otherwise the lanes slide away from the rows they belong to.
    /// </summary>
    private void SetUpCommitGraph()
    {
        CommitList.Loaded += (_, _) =>
        {
            if (CommitList.FindDescendantOfType<ScrollViewer>() is { } scroller)
            {
                scroller.ScrollChanged += (_, _) => GraphView.ScrollOffset = scroller.Offset.Y;
            }
        };
    }

    private WorkspaceViewModel? Workspace => (DataContext as MainViewModel)?.Workspace;

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
        }

        UpdateDiffEditor();
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

        // The native folder dialog takes the foreground, which would otherwise trip
        // hide-on-deactivate and make the launcher vanish behind the picker.
        using var _ = (Application.Current as App)?.SuppressAutoHide();

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

    private void ScrollSelectionIntoView(MainViewModel vm)
    {
        if (vm.SelectedRepo is { } selected)
        {
            RepoList.ScrollIntoView(selected);
        }
    }
}
