using System.Windows.Controls;
using BusinessERP.Desktop.Services;
using BusinessERP.Desktop.ViewModels;

namespace BusinessERP.Desktop.Views;

public partial class ReportsWorkspaceControl : UserControl
{
    private readonly ReportsWorkspaceViewModel _viewModel = new();

    public ReportsWorkspaceControl()
    {
        InitializeComponent();
        DataContext = _viewModel;
    }

    public Task SetApiClientAsync(ErpApiClient? apiClient) => _viewModel.SetApiClientAsync(apiClient);

    public Task RefreshAsync() => _viewModel.RefreshAsync();

    public string WorkspaceStatusMessage => _viewModel.StatusMessage;
}
