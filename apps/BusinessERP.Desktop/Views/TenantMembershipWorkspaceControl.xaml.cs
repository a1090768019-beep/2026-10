using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using BusinessERP.Desktop.Services;
using BusinessERP.Desktop.ViewModels;

namespace BusinessERP.Desktop.Views;

public partial class TenantMembershipWorkspaceControl : UserControl
{
    private readonly TenantMembershipWorkspaceViewModel _viewModel = new();

    public TenantMembershipWorkspaceControl()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    public event EventHandler? WorkspaceContextChanged;
    public string WorkspaceContextSummary => _viewModel.WorkspaceContextSummary;
    public string WorkspaceStatusMessage => _viewModel.StatusMessage;

    public Task SetApiClientAsync(ErpApiClient? apiClient) => _viewModel.SetApiClientAsync(apiClient);
    public Task RefreshAsync() => _viewModel.RefreshAsync();

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TenantMembershipWorkspaceViewModel.WorkspaceContextSummary)
            or nameof(TenantMembershipWorkspaceViewModel.SelectedMember)
            or nameof(TenantMembershipWorkspaceViewModel.StatusMessage))
        {
            WorkspaceContextChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) =>
        await _viewModel.RefreshAsync();

    private async void AssignMember_Click(object sender, RoutedEventArgs e) =>
        await _viewModel.AssignMemberAsync();

    private async void UpdateMember_Click(object sender, RoutedEventArgs e) =>
        await _viewModel.UpdateMemberAsync();
}
