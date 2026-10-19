import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { resolve } from 'node:path';

const root = resolve(import.meta.dirname, '../..');
const read = (path) => readFile(resolve(root, path), 'utf8');

test('desktop CashBank workspace creates and posts receipts and supplier payments through tenant API', async () => {
  const client = await read('apps/BusinessERP.Desktop/Services/ErpApiClient.cs');
  const viewModel = await read('apps/BusinessERP.Desktop/ViewModels/SettlementsWorkspaceViewModel.cs');
  const view = await read('apps/BusinessERP.Desktop/Views/SettlementsWorkspaceControl.xaml');
  const codeBehind = await read('apps/BusinessERP.Desktop/Views/SettlementsWorkspaceControl.xaml.cs');
  const shell = await read('apps/BusinessERP.Desktop/MainWindow.xaml');
  const navigation = await read('apps/BusinessERP.Desktop/MainWindow.xaml.cs');

  assert.match(client, /GetCustomerReceiptsAsync/);
  assert.match(client, /CreateCustomerReceiptAsync/);
  assert.match(client, /PostCustomerReceiptAsync/);
  assert.match(client, /GetSupplierPaymentsAsync/);
  assert.match(client, /CreateSupplierPaymentAsync/);
  assert.match(client, /PostSupplierPaymentAsync/);
  assert.match(client, /GetOutstandingSalesInvoicesAsync/);
  assert.match(client, /GetOutstandingPurchaseInvoicesAsync/);
  assert.match(client, /OutstandingInvoiceApiDto/);
  assert.match(viewModel, /SelectedSalesInvoice\.CustomerId == SelectedCustomer\.Id/);
  assert.match(viewModel, /SelectedPurchaseInvoice\.SupplierId == SelectedSupplier\.Id/);
  assert.match(viewModel, /SelectedPeriod\?\.Status == "OPEN"/);
  assert.match(viewModel, /CreateCustomerReceiptApiRequest/);
  assert.match(viewModel, /PostSupplierPaymentApiRequest/);
  assert.match(view, /Command="{Binding CreateReceiptCommand}"/);
  assert.match(view, /DisplayMemberPath="DisplayLabel"/);
  assert.match(view, /Command="{Binding PostReceiptCommand}"/);
  assert.match(view, /Command="{Binding CreatePaymentCommand}"/);
  assert.match(view, /Command="{Binding PostPaymentCommand}"/);
  assert.match(codeBehind, /LoadSelectedCompanyAsync/);
  assert.match(shell, /SettlementsWorkspaceControl/);
  assert.match(navigation, /SettlementsWorkspacePanel\.Visibility = Visibility\.Visible/);
  assert.match(navigation, /await SettlementsWorkspacePanel\.SetApiClientAsync\(null\)/);
});
