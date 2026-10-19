import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { resolve } from 'node:path';

const root = resolve(import.meta.dirname, '../..');
const read = (path) => readFile(resolve(root, path), 'utf8');

test('sales and purchase VAT details are paged, labelled as internal, and export the current page', async () => {
  const client = await read('apps/BusinessERP.Desktop/Services/ErpApiClient.cs');
  const viewModel = await read('apps/BusinessERP.Desktop/ViewModels/ReportsWorkspaceViewModel.cs');
  const view = await read('apps/BusinessERP.Desktop/Views/ReportsWorkspaceControl.xaml');
  const codeBehind = await read('apps/BusinessERP.Desktop/Views/ReportsWorkspaceControl.xaml.cs');
  const api = await read('src/server/BusinessERP.Api/Endpoints/PurchaseDraftVatReportEndpoints.cs');

  assert.match(client, /GetSalesVatDetailsReportAsync/);
  assert.match(client, /GetPurchaseVatDetailsReportAsync/);
  assert.match(viewModel, /GetSalesVatDetailsReportAsync/);
  assert.match(viewModel, /GetPurchaseVatDetailsReportAsync/);
  assert.match(viewModel, /PreviousSalesVatPageCommand/);
  assert.match(viewModel, /NextSalesVatPageCommand/);
  assert.match(viewModel, /PreviousPurchaseVatPageCommand/);
  assert.match(viewModel, /NextPurchaseVatPageCommand/);
  assert.match(viewModel, /ApplyPurchaseVatReport/);
  assert.match(view, /Header="تفاصيل ضريبة المبيعات"/);
  assert.match(view, /Header="تفاصيل ضريبة المشتريات"/);
  assert.match(view, /ItemsSource="{Binding SalesVatRows}"/);
  assert.match(view, /ItemsSource="{Binding PurchaseVatRows}"/);
  assert.match(view, /لا يثبت قابلية استرداد ضريبة المدخلات/);
  assert.match(codeBehind, /ReportsTabs\.SelectedIndex == 3.*CurrentSalesVatDetailsReport/s);
  assert.match(codeBehind, /ReportsTabs\.SelectedIndex == 4.*CurrentPurchaseVatDetailsReport/s);
  assert.match(codeBehind, /return \("PurchaseVatDetails", rows\)/);
  assert.match(api, /GetPurchaseVatDetailsReport/);
  assert.match(api, /tax_calculation_status = 'CALCULATED'/);
  assert.match(api, /tenant\.TenantId/);
});
