import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { resolve } from 'node:path';

const root = resolve(import.meta.dirname, '../..');
const read = (path) => readFile(resolve(root, path), 'utf8');

test('supplier and purchase drafts are tenant-scoped and protected by PostgreSQL constraints', async () => {
  const migration = await read('database/migrations/0005_purchase_invoice_drafts.sql');
  const runtimeRole = await read('database/tests/prepare-runtime-role.sql');
  const ci = await read('.github/workflows/ci.yml');
  const program = await read('src/server/BusinessERP.Api/Program.cs');
  const suppliers = await read('src/server/BusinessERP.Api/Endpoints/SupplierEndpoints.cs');
  const drafts = await read('src/server/BusinessERP.Api/Endpoints/PurchaseInvoiceDraftEndpoints.cs');

  assert.match(migration, /CREATE TABLE suppliers/);
  assert.match(migration, /CREATE TABLE purchase_invoice_drafts/);
  assert.match(migration, /CREATE TABLE purchase_invoice_draft_lines/);
  assert.match(migration, /ENABLE ROW LEVEL SECURITY/);
  assert.match(migration, /FORCE ROW LEVEL SECURITY/);
  assert.match(migration, /assert_purchase_invoice_draft_totals/);
  assert.match(migration, /DEFERRABLE INITIALLY DEFERRED/);
  assert.match(migration, /supplier_invoice_number/);
  assert.match(runtimeRole, /GRANT SELECT, INSERT ON suppliers, purchase_invoice_drafts, purchase_invoice_draft_lines/);
  assert.match(runtimeRole, /GRANT UPDATE \([\s\S]*tax_calculation_status[\s\S]*\) ON purchase_invoice_drafts/);
  assert.match(ci, /database\/migrations\/0005_purchase_invoice_drafts\.sql/);
  assert.match(program, /MapSupplierEndpoints/);
  assert.match(program, /MapPurchaseInvoiceDraftEndpoints/);
  assert.match(suppliers, /TenantTransactionRunner/);
  assert.match(suppliers, /SUPPLIER_CREATED/);
  assert.match(suppliers, /supplier-code:/);
  assert.match(drafts, /purchase-invoice-drafts/);
  assert.match(drafts, /supplier_invoice_number_conflict/);
  assert.match(drafts, /PURCHASE_INVOICE_DRAFT_CREATED/);
  assert.match(drafts, /pg_advisory_xact_lock/);
});

test('purchase VAT calculation is persisted using the shared calculator and reported with bounded pagination', async () => {
  const vat = await read('src/server/BusinessERP.Api/Endpoints/PurchaseDraftVatEndpoints.cs');
  const report = await read('src/server/BusinessERP.Api/Endpoints/PurchaseDraftVatReportEndpoints.cs');
  const program = await read('src/server/BusinessERP.Api/Program.cs');

  assert.match(vat, /SaudiVatCalculator\.Calculate/);
  assert.match(vat, /purchase_invoice_draft_id/);
  assert.match(vat, /tax_calculation_status = 'CALCULATED'/);
  assert.match(vat, /PURCHASE_DRAFT_VAT_CALCULATED/);
  assert.match(vat, /recoverability|recoverable|قابلية استرداد/i);
  assert.match(report, /reports\/purchase-vat-details/);
  assert.match(report, /invoice\.tenant_id = \{tenant\.TenantId\}/);
  assert.match(report, /invoice\.company_id = \{companyId\}/);
  assert.match(report, /invoice\.issue_date >= \{fromDate\}/);
  assert.match(report, /invoice\.issue_date <= \{toDate\}/);
  assert.match(report, /line\.purchase_invoice_draft_id = invoice\.id/);
  assert.match(report, /LIMIT \{pageSize\} OFFSET \{offset\}/);
  assert.match(report, /MaximumPageSize = 200/);
  assert.match(report, /INTERNAL_PURCHASE_DRAFT_VAT_REPORT/);
  assert.doesNotMatch(report, /sales_invoice_draft_id/);
  assert.match(program, /MapPurchaseDraftVatEndpoints/);
  assert.match(program, /MapPurchaseDraftVatReportEndpoints/);
});

test('desktop purchases workspace calls API-backed supplier, draft, VAT and report operations', async () => {
  const client = await read('apps/BusinessERP.Desktop/Services/ErpApiClient.cs');
  const viewModel = await read('apps/BusinessERP.Desktop/ViewModels/PurchasesWorkspaceViewModel.cs');
  const view = await read('apps/BusinessERP.Desktop/Views/PurchasesWorkspaceControl.xaml');
  const codeBehind = await read('apps/BusinessERP.Desktop/Views/PurchasesWorkspaceControl.xaml.cs');
  const shell = await read('apps/BusinessERP.Desktop/MainWindow.xaml');
  const main = await read('apps/BusinessERP.Desktop/MainWindow.xaml.cs');

  assert.match(client, /GetSuppliersAsync/);
  assert.match(client, /CreatePurchaseInvoiceDraftAsync/);
  assert.match(client, /CalculateAndPersistPurchaseDraftVatAsync/);
  assert.match(client, /GetPurchaseVatDetailsReportAsync/);
  assert.match(viewModel, /CreateSupplierCommand/);
  assert.match(viewModel, /CreatePurchaseDraftCommand/);
  assert.match(viewModel, /CalculateSelectedDraftVatCommand/);
  assert.match(viewModel, /LoadVatReportCommand/);
  assert.match(view, /ItemsSource="{Binding Suppliers}"/);
  assert.match(view, /ItemsSource="{Binding PurchaseDrafts}"/);
  assert.match(view, /ItemsSource="{Binding PurchaseVatRows}"/);
  assert.match(view, /NewPurchase_Click/);
  assert.match(codeBehind, /BeginNewPurchaseDraft/);
  assert.match(shell, /PurchasesWorkspacePanel/);
  assert.match(shell, /x:Name="PurchasesWorkspaceTab"/);
  assert.match(main, /string\\.Equals\\(key, "Purchases", StringComparison\\.OrdinalIgnoreCase\\)/);
  assert.match(main, /PurchasesWorkspacePanel\.SetApiClientAsync/);
});
