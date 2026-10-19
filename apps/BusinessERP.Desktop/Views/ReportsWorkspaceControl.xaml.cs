using System.Windows.Controls;
using System.ComponentModel;
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
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    public event EventHandler? WorkspaceContextChanged;

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ReportsWorkspaceViewModel.WorkspaceContextSummary))
            WorkspaceContextChanged?.Invoke(this, EventArgs.Empty);
    }

    public string WorkspaceContextSummary => _viewModel.WorkspaceContextSummary;

    public Task SetApiClientAsync(ErpApiClient? apiClient) => _viewModel.SetApiClientAsync(apiClient);

    public Task RefreshAsync() => _viewModel.RefreshAsync();

    public string WorkspaceStatusMessage => _viewModel.StatusMessage;
}
