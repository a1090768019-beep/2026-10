using System.Windows.Controls;
using BusinessERP.Desktop.Services;
using BusinessERP.Desktop.ViewModels;

namespace BusinessERP.Desktop.Views;

public partial class PurchasesWorkspaceControl : UserControl
{
    private readonly PurchasesWorkspaceViewModel _viewModel = new();

    public PurchasesWorkspaceControl()
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
    public void BeginNewPurchaseDraft() => _viewModel.BeginNewPurchaseDraft();
}
