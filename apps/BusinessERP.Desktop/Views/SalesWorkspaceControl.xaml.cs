using System.Windows;
using System.Windows.Controls;
using BusinessERP.Desktop.Services;
using BusinessERP.Desktop.ViewModels;

namespace BusinessERP.Desktop.Views;

public partial class SalesWorkspaceControl : UserControl
{
    private readonly SalesWorkspaceViewModel _viewModel = new();

    public SalesWorkspaceControl()
    {
        InitializeComponent();
        DataContext = _viewModel;
    }

    public Task SetApiClientAsync(ErpApiClient? apiClient) => _viewModel.SetApiClientAsync(apiClient);

    public Task RefreshAsync() => _viewModel.RefreshAsync();

    private async void CreateInvoiceDraftButton_Click(object sender, RoutedEventArgs e)
    {
        InvoiceLinesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        InvoiceLinesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        await _viewModel.CreateInvoiceDraftAsync();
    }
}
