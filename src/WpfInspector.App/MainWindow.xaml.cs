using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using WpfInspector.App.ViewModels;
using WpfInspector.Core.Services;

namespace WpfInspector.App;

public partial class MainWindow : Window
{
    private MainViewModel ViewModel => (MainViewModel)DataContext;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel(
            new ProcessInspectionService(new PeMetadataService()),
            new PortablePdbAnalysisService());
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
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

    private async void UseSelectedModulePdbButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.TryUseSelectedModulePdb())
        {
            return;
        }

        await ViewModel.AnalyzePdbAsync();
    }
}
