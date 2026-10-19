import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { resolve } from 'node:path';

const root = resolve(import.meta.dirname, '../..');
const read = (path) => readFile(resolve(root, path), 'utf8');

test('sales VAT detail report uses saved calculations and tenant/company/date scoping', async () => {
  const endpoint = await read('src/server/BusinessERP.Api/Endpoints/SalesDraftVatReportEndpoints.cs');
  const program = await read('src/server/BusinessERP.Api/Program.cs');
  const client = await read('apps/BusinessERP.Desktop/Services/ErpApiClient.cs');
  const viewModel = await read('apps/BusinessERP.Desktop/ViewModels/ReportsWorkspaceViewModel.cs');
  const view = await read('apps/BusinessERP.Desktop/Views/ReportsWorkspaceControl.xaml');
  const code = await read('apps/BusinessERP.Desktop/Views/ReportsWorkspaceControl.xaml.cs');

  assert.match(endpoint, /reports\/sales-vat-details/);
  assert.match(endpoint, /invoice\.tenant_id = \{tenant\.TenantId\}/);
  assert.match(endpoint, /invoice\.company_id = \{companyId\}/);
  assert.match(endpoint, /issue_date >= \{fromDate\}/);
  assert.match(endpoint, /issue_date <= \{toDate\}/);
  assert.match(endpoint, /tax_calculation_status = 'CALCULATED'/);
  assert.match(endpoint, /LIMIT \{pageSize\} OFFSET \{offset\}/);
  assert.match(endpoint, /INTERNAL_SALES_DRAFT_VAT_REPORT/);
  assert.match(program, /MapSalesDraftVatReportEndpoints/);
  assert.match(client, /GetSalesVatDetailsReportAsync/);
  assert.match(viewModel, /ChangeSalesVatPageAsync/);
  assert.match(view, /ItemsSource="{Binding SalesVatRows}"/);
  assert.match(view, /لا يمثل فاتورة ضريبية نهائية/);
  assert.match(code, /"SalesVatDetails"/);
});
