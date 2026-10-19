using System.Windows;
using System.Windows.Controls;
using BusinessERP.Desktop.Services;
using BusinessERP.Desktop.ViewModels;

namespace BusinessERP.Desktop.Views;

public partial class SettlementsWorkspaceControl : UserControl
{
    private readonly SettlementsWorkspaceViewModel _viewModel = new();

    public SettlementsWorkspaceControl()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.WorkspaceContextChanged += (_, _) => WorkspaceContextChanged?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? WorkspaceContextChanged;
    public string WorkspaceContextSummary => _viewModel.WorkspaceContextSummary;
    public string WorkspaceStatusMessage => _viewModel.StatusMessage;

    public Task SetApiClientAsync(ErpApiClient? apiClient) => _viewModel.SetApiClientAsync(apiClient);
    public Task RefreshAsync() => _viewModel.RefreshAsync();

    private async void Company_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && _viewModel.SelectedCompany is not null)
            await _viewModel.LoadSelectedCompanyAsync();
    }
}
