using LintelAI.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace LintelAI.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        _viewModel = new MainViewModel(new DialogService(this));
        DataContext = _viewModel;

        // The custom header only suits Windows, where the caption buttons sit at the right edge.
        // Elsewhere keep the native title bar rather than guess at its layout.
        if (!OperatingSystem.IsWindows())
        {
            ExtendClientAreaToDecorationsHint = false;
            HeaderLayout.Margin = new Thickness(20, 0, 20, 0);
        }

        Opened += async (_, _) => await _viewModel.InitializeAsync();
        Closed += (_, _) => _viewModel.Dispose();

        // Drag the window by its custom header; buttons inside it keep working normally.
        Header.PointerPressed += (_, e) =>
        {
            if (e.Source is Visual source &&
                !source.GetSelfAndVisualAncestors().OfType<Button>().Any() &&
                e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                BeginMoveDrag(e);
            }
        };

        KeyDown += OnWindowKeyDown;
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
        {
            var search = this.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Name == "SearchBox");
            if (search is not null)
            {
                search.Focus();
                search.SelectAll();
                e.Handled = true;
            }
        }
    }
}
