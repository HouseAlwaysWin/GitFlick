using System;
using System.ComponentModel;
using Avalonia.Controls;
using GitFlick.ViewModels;

namespace GitFlick.Views;

/// <summary>
/// A standalone two-ref comparison: the commits and files that <c>compare</c> carries beyond
/// <c>base</c>, with a per-file range diff. Bound to a dedicated <see cref="CompareViewModel"/>;
/// the diff text is pushed into the AvaloniaEdit editor from code-behind, as in <see cref="MainWindow"/>.
/// </summary>
public partial class CompareWindow : Window
{
    private CompareViewModel? _observed;

    public CompareWindow()
    {
        InitializeComponent();
        DiffEditorSetup.Apply(DiffEditor);
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_observed is not null)
        {
            _observed.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _observed = DataContext as CompareViewModel;

        if (_observed is not null)
        {
            _observed.PropertyChanged += OnViewModelPropertyChanged;
            UpdateDiffEditor();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CompareViewModel.DiffText))
        {
            UpdateDiffEditor();
        }
    }

    private void UpdateDiffEditor() => DiffEditor.Text = _observed?.DiffText ?? string.Empty;
}
