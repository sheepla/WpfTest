using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WpfInspector.App.Services;
using WpfInspector.App.ViewModels;
using WpfInspector.Core.Models;
using WpfInspector.Core.Services;

namespace WpfInspector.App;

public partial class MainWindow : Window
{
    private bool _fixturesInitialized;

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    public MainWindow()
    {
        InitializeComponent();

        var viewModel = new MainViewModel(
            new ProcessInspectionService(new PeMetadataService()),
            new PortablePdbAnalysisService(),
            new WpfInjectionService());

        viewModel.FixtureLab.ResourcePaletteChanged += FixtureLab_OnResourcePaletteChanged;
        DataContext = viewModel;

        ApplyFixturePalette(viewModel.FixtureLab.ActivePalette);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        InitializeFixtureSurfaces();
        await ViewModel.RefreshProcessesAsync();
        await ViewModel.LoadSelectedProcessAsync();
    }

    private async void RefreshButton_OnClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.RefreshProcessesAsync();
        await ViewModel.LoadSelectedProcessAsync();
    }

    private async void ProcessesGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel.SelectedProcess is null)
        {
            return;
        }

        await ViewModel.LoadSelectedProcessAsync();
    }

    private void BrowsePdbButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "PDB files (*.pdb)|*.pdb|All files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            ViewModel.PdbPath = dialog.FileName;
        }
    }

    private void BrowseAssemblyButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Assemblies (*.dll;*.exe)|*.dll;*.exe|All files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            ViewModel.AssemblyPath = dialog.FileName;
        }
    }

    private async void AnalyzePdbButton_OnClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.AnalyzePdbAsync();
    }

    private async void InjectButton_OnClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.AnalyzeSelectedWpfAsync();
    }

    private void InspectionTree_OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        ViewModel.SelectInspectedElement(e.NewValue as WpfElementInfo);
    }

    private void FixtureLab_OnResourcePaletteChanged(object? sender, ResourcePaletteChangedEventArgs e)
    {
        ApplyFixturePalette(e.Palette);
    }

    private void InitializeFixtureSurfaces()
    {
        if (_fixturesInitialized)
        {
            return;
        }

        _fixturesInitialized = true;
        ValidationFixtureTextBox.Text = "bad";
        ValidationFixtureTextBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }

    private void ApplyFixturePalette(ResourcePalette palette)
    {
        var (accent, surface) = palette switch
        {
            ResourcePalette.Alert => (Color.FromRgb(0xA7, 0x2E, 0x1B), Color.FromRgb(0xFE, 0xF1, 0xEB)),
            ResourcePalette.Forest => (Color.FromRgb(0x1D, 0x6B, 0x44), Color.FromRgb(0xEB, 0xF7, 0xEF)),
            _ => (Color.FromRgb(0x0F, 0x6C, 0xBD), Color.FromRgb(0xEB, 0xF3, 0xFF))
        };

        Resources["FixtureAccentBrush"] = new SolidColorBrush(accent);
        Resources["FixtureSurfaceBrush"] = new SolidColorBrush(surface);
    }
}
