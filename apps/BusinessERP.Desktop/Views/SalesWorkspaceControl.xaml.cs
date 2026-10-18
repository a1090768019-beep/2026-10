using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    public bool SearchLoadedData(string searchText) => _viewModel.SearchLoadedData(searchText);

    public string WorkspaceStatusMessage => _viewModel.StatusMessage;

    public void BeginNewDraft()
    {
        _viewModel.PrepareNewDraft();
        InvoiceNumberTextBox.Focus();
        Keyboard.Focus(InvoiceNumberTextBox);
    }

    public async Task SaveCurrentDraftAsync()
    {
        InvoiceLinesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        InvoiceLinesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        await _viewModel.CreateInvoiceDraftAsync();
    }

    public Task PostSelectedDraftToLedgerAsync() => _viewModel.PostSelectedDraftToLedgerAsync();

    private async void CreateInvoiceDraftButton_Click(object sender, RoutedEventArgs e)
    {
        await SaveCurrentDraftAsync();
    }

    private async void CalculateVatPreviewButton_Click(object sender, RoutedEventArgs e)
    {
        InvoiceLinesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        InvoiceLinesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        await _viewModel.CalculateVatPreviewAsync();
    }
}
