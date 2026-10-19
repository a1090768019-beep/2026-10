import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { resolve } from 'node:path';

const root = resolve(import.meta.dirname, '../..');
const read = (path) => readFile(resolve(root, path), 'utf8');

test('database bootstrap defines and forces RLS for tenant-owned tables', async () => {
  const sql = await read('database/migrations/0001_initial.sql');
  assert.match(sql, /ALTER TABLE tenants ENABLE ROW LEVEL SECURITY/);
  assert.match(sql, /USING \(id = app_current_tenant_id\(\)\)/);
  assert.match(sql, /ENABLE ROW LEVEL SECURITY/);
  assert.match(sql, /FORCE ROW LEVEL SECURITY/);
  assert.match(sql, /app_current_tenant_id\(\)/);
  assert.match(sql, /WITH CHECK \(tenant_id = app_current_tenant_id\(\)\)/);
  for (const table of ['companies', 'accounts', 'journal_entries', 'journal_entry_lines', 'tenant_memberships']) {
    assert.ok(sql.includes(`'${table}'`), `RLS table list is missing ${table}`);
  }
});

test('money columns use exact PostgreSQL numeric types, not floating point', async () => {
  const sql = await read('database/migrations/0001_initial.sql');
  assert.match(sql, /debit numeric\(20,4\)/);
  assert.match(sql, /credit numeric\(20,4\)/);
  assert.doesNotMatch(sql, /^\s*[a-z_][a-z0-9_]*\s+(?:double precision|real)\b/im);
});

test('journal entries support duplicate prevention and request-hash idempotency', async () => {
  const sql = await read('database/migrations/0001_initial.sql');
  const api = await read('src/server/BusinessERP.Api/Endpoints/AccountingEndpoints.cs');
  assert.match(sql, /ux_journal_idempotency/);
  assert.match(sql, /idempotency_request_hash varchar\(64\)/);
  assert.match(sql, /ux_journal_reversal/);
  assert.match(sql, /CHECK \(\(debit > 0 AND credit = 0\) OR \(credit > 0 AND debit = 0\)\)/);
  assert.match(api, /idempotency_key_reused/);
  assert.match(api, /SHA256/);
});

test('desktop application is native C# WPF and uses a configurable API URL', async () => {
  const project = await read('apps/BusinessERP.Desktop/BusinessERP.Desktop.csproj');
  const xaml = await read('apps/BusinessERP.Desktop/MainWindow.xaml');
  const codeBehind = await read('apps/BusinessERP.Desktop/MainWindow.xaml.cs');
  const service = await read('apps/BusinessERP.Desktop/Services/ApiHealthService.cs');
  const settings = await read('apps/BusinessERP.Desktop/appsettings.json');
  assert.match(project, /<UseWPF>true<\/UseWPF>/);
  assert.match(project, /net10\.0-windows/);
  assert.match(xaml, /FlowDirection="RightToLeft"/);
  assert.match(codeBehind, /Navigate_Click/);
  assert.match(service, /health\/live/);
  assert.match(settings, /ApiBaseUrl/);
  assert.doesNotMatch(xaml + codeBehind, /رصيد اليوم\s*[:：]?\s*[0-9]/);
});

test('API routes implement company/account/fiscal/journal/trial-balance workflows', async () => {
  const api = await read('src/server/BusinessERP.Api/Endpoints/AccountingEndpoints.cs');
  for (const path of [
    '"/companies"', '"/companies/{companyId:guid}/accounts"',
    '"/companies/{companyId:guid}/fiscal-years"', '"/companies/{companyId:guid}/fiscal-periods"',
    '"/companies/{companyId:guid}/journal-entries"', '"/journal-entries/{entryId:guid}"', '"/journal-entries/{entryId:guid}/approve"',
    '"/journal-entries/{entryId:guid}/post"', '"/journal-entries/{entryId:guid}/reverse"',
    '"/companies/{companyId:guid}/reports/trial-balance"'
  ]) assert.ok(api.includes(path), `Missing API route ${path}`);
  assert.match(api, /JournalEntryValidator\.Validate/);
  assert.match(api, /UpdateDraftJournalEntry/);
  assert.match(api, /FiscalPeriodId/);
  assert.match(api, /JOURNAL_ENTRY_POSTED/);
  assert.match(api, /JOURNAL_ENTRY_REVERSED/);
});

test('API verifies the validated identity and active tenant membership before routes', async () => {
  const program = await read('src/server/BusinessERP.Api/Program.cs');
  const middleware = await read('src/server/BusinessERP.Api/Security/TenantResolutionMiddleware.cs');
  const membership = await read('src/server/BusinessERP.Infrastructure/Tenancy/TenantMembershipVerifier.cs');
  assert.match(program, /Authentication:Authority/);
  assert.match(program, /UseMiddleware<TenantResolutionMiddleware>/);
  assert.match(middleware, /Identity\?\.IsAuthenticated/);
  assert.match(middleware, /X-Tenant-Id/);
  assert.match(middleware, /DisabledAt == null/);
  assert.match(middleware, /GetActiveRoleAsync/);
  assert.match(membership, /set_config\('app\.tenant_id'/);
});

test('posting and audit immutability have database backstops', async () => {
  const sql = await read('database/migrations/0001_initial.sql');
  assert.match(sql, /CREATE TRIGGER trg_journal_entry_state/);
  assert.match(sql, /Only an approved journal entry can be posted/);
  assert.match(sql, /Journal entry must have at least two lines and equal debit\/credit totals/);
  assert.match(sql, /CREATE TRIGGER trg_journal_entry_line_mutation/);
  assert.match(sql, /CREATE TRIGGER trg_audit_logs_append_only/);
  assert.match(sql, /role_code varchar\(24\).*OWNER.*ADMIN.*ACCOUNTANT.*VIEWER/);
});


test('industry chart templates are seeded as database reference data and applied idempotently only to empty companies', async () => {
  const sql = await read('database/migrations/0001_initial.sql');
  const endpoints = await read('src/server/BusinessERP.Api/Endpoints/ChartOfAccountsTemplateEndpoints.cs');
  const program = await read('src/server/BusinessERP.Api/Program.cs');
  for (const industry of ['TRADE', 'SERVICES', 'CONTRACTING', 'RESTAURANTS', 'MANUFACTURING', 'REAL_ESTATE']) {
    assert.ok(sql.includes(`'${industry}'`), `Missing seeded chart template ${industry}`);
  }
  assert.match(sql, /CREATE TABLE account_templates/);
  assert.match(sql, /CREATE TABLE account_template_accounts/);
  assert.match(endpoints, /chart_of_accounts_not_empty/);
  assert.match(endpoints, /ACCOUNT_TEMPLATE_APPLIED/);
  assert.match(endpoints, /CanApplyTemplateRoles/);
  assert.match(endpoints, /ParentCode/);
  assert.match(program, /MapChartOfAccountsTemplateEndpoints/);
});

test('concurrent accounting uniqueness checks are serialized inside the transaction', async () => {
  const api = await read('src/server/BusinessERP.Api/Endpoints/AccountingEndpoints.cs');
  assert.match(api, /pg_advisory_xact_lock\(hashtextextended/);
  assert.match(api, /journal-entry-number:/);
  assert.match(api, /journal-entry-idempotency:/);
  assert.match(api, /journal-reversal:/);
  assert.match(api, /fiscal-year-overlap:/);
  assert.match(api, /Keep lock ordering stable/);
});

test('SaaS subscription endpoints expose active plans and tenant-scoped subscription status without claiming billing integration', async () => {
  const api = await read('src/server/BusinessERP.Api/Endpoints/SubscriptionEndpoints.cs');
  const program = await read('src/server/BusinessERP.Api/Program.cs');
  const context = await read('src/server/BusinessERP.Infrastructure/Persistence/ErpDbContext.cs');
  const records = await read('src/server/BusinessERP.Infrastructure/Persistence/Records.cs');
  assert.match(program, /MapSubscriptionEndpoints/);
  assert.match(api, /"\/subscription-plans"/);
  assert.match(api, /"\/subscription"/);
  assert.match(api, /s\.TenantId == tenant\.TenantId/);
  assert.match(api, /Status\.Equals\("TRIALING"/);
  assert.match(api, /Status\.Equals\("ACTIVE"/);
  assert.match(api, /تمثل تحصيلًا/);
  assert.match(context, /DbSet<SubscriptionPlanRecord>/);
  assert.match(context, /DbSet<TenantSubscriptionRecord>/);
  assert.match(records, /class SubscriptionPlanRecord/);
  assert.match(records, /class TenantSubscriptionRecord/);
});


test('sales invoice drafts are tenant-scoped, exact-decimal internal documents and are migrated before runtime-role setup', async () => {
  const migration = await read('database/migrations/0002_sales_invoice_drafts.sql');
  const role = await read('database/tests/prepare-runtime-role.sql');
  const ci = await read('.github/workflows/ci.yml');
  const api = await read('src/server/BusinessERP.Api/Endpoints/SalesInvoiceDraftEndpoints.cs');
  const customerApi = await read('src/server/BusinessERP.Api/Endpoints/CustomerEndpoints.cs');
  const program = await read('src/server/BusinessERP.Api/Program.cs');
  assert.match(migration, /CREATE TABLE customers/);
  assert.match(migration, /CREATE TABLE sales_invoice_drafts/);
  assert.match(migration, /CREATE TABLE sales_invoice_draft_lines/);
  assert.match(migration, /numeric\(20,4\)/);
  assert.match(migration, /DEFERRABLE INITIALLY DEFERRED/);
  assert.match(migration, /ENABLE ROW LEVEL SECURITY/);
  assert.match(migration, /WITH CHECK \(tenant_id = app_current_tenant_id\(\)\)/);
  assert.match(role, /GRANT SELECT, INSERT ON customers, sales_invoice_drafts, sales_invoice_draft_lines/);
  assert.match(ci, /database\/migrations\/0002_sales_invoice_drafts\.sql/);
  assert.match(customerApi, /CUSTOMER_CREATED/);
  assert.match(customerApi, /customer-code:/);
  assert.match(api, /sales-invoice-number:/);
  assert.match(api, /SALES_INVOICE_DRAFT_CREATED/);
  assert.match(api, /MidpointRounding\.AwayFromZero/);
  assert.match(program, /MapSalesInvoiceDraftEndpoints/);
});

test('income statement and balance sheet reports derive only from posted ledger entries', async () => {
  const api = await read('src/server/BusinessERP.Api/Endpoints/AccountingEndpoints.cs');
  assert.match(api, /reports\/income-statement/);
  assert.match(api, /reports\/balance-sheet/);
  assert.match(api, /entry\.Status == "POSTED"/);
  assert.match(api, /LoadPostedAccountMovementsAsync/);
  assert.match(api, /totalAssets, totalLiabilities, totalEquity/);
  assert.match(api, /totalAssets - totalLiabilities - totalEquity/);
});


test('Saudi VAT preview requires explicit treatment, decimal rounding and tenant company validation', async () => {
  const calculator = await read('src/server/BusinessERP.Domain/Tax/SaudiVatCalculator.cs');
  const endpoint = await read('src/server/BusinessERP.Api/Endpoints/SalesVatPreviewEndpoints.cs');
  const program = await read('src/server/BusinessERP.Api/Program.cs');
  assert.match(calculator, /StandardRatePercent = 15m/);
  assert.match(calculator, /MidpointRounding\.AwayFromZero/);
  assert.match(calculator, /ZeroRated\s*=\s*2/);
  assert.match(calculator, /TreatmentReason/);
  assert.match(endpoint, /sales-vat-preview/);
  assert.match(endpoint, /PREVIEW_ONLY/);
  assert.match(endpoint, /db\.Companies\.AsNoTracking\(\)/);
  assert.match(endpoint, /SaudiVatCalculator\.Calculate/);
  assert.match(program, /MapSalesVatPreviewEndpoints/);
});


test('WPF sales workspace sends VAT preview to the API and labels it as non-persistent', async () => {
  const apiClient = await read('apps/BusinessERP.Desktop/Services/ErpApiClient.cs');
  const viewModel = await read('apps/BusinessERP.Desktop/ViewModels/SalesWorkspaceViewModel.cs');
  const view = await read('apps/BusinessERP.Desktop/Views/SalesWorkspaceControl.xaml');
  const codeBehind = await read('apps/BusinessERP.Desktop/Views/SalesWorkspaceControl.xaml.cs');
  assert.match(apiClient, /sales-vat-preview/);
  assert.match(viewModel, /CalculateSalesVatPreviewAsync/);
  assert.match(viewModel, /TreatmentReason/);
  assert.match(viewModel, /PREVIEW_ONLY|preview/i);
  assert.match(view, /CalculateVatPreviewButton_Click/);
  assert.match(view, /المعاينة لا تُحفظ بذاتها/);
  assert.match(view, /DisplayMemberPath="Label"/);
  assert.match(codeBehind, /await _viewModel\.CalculateVatPreviewAsync\(\)/);
  assert.match(viewModel, /CalculateAndPersistSalesDraftVatAsync/);
  assert.match(apiClient, /sales-invoice-drafts\/{invoiceId:D}\/calculate-vat/);
});


test('persisted VAT endpoint calculates from stored invoice lines and updates header plus lines atomically', async () => {
  const endpoint = await read('src/server/BusinessERP.Api/Endpoints/SalesDraftVatEndpoints.cs');
  const migration = await read('database/migrations/0003_sales_invoice_draft_vat.sql');
  const program = await read('src/server/BusinessERP.Api/Program.cs');
  assert.match(endpoint, /sales-invoice-drafts\/{invoiceId:guid}\/calculate-vat/);
  assert.match(endpoint, /SaudiVatCalculator\.Calculate/);
  assert.match(endpoint, /SELECT id AS "Id"[\s\S]*FROM sales_invoice_draft_lines/);
  assert.match(endpoint, /UPDATE sales_invoice_drafts[\s\S]*tax_calculation_status = 'CALCULATED'/);
  assert.match(endpoint, /SALES_DRAFT_VAT_CALCULATED/);
  assert.match(endpoint, /TenantTransactionRunner/);
  assert.match(migration, /ck_sales_invoice_draft_tax_totals/);
  assert.match(migration, /actual_vat <> expected_vat/);
  assert.match(program, /MapSalesDraftVatEndpoints/);
});


test('sales draft GL posting is tenant-bound, balanced, immutable after post and idempotent by source', async () => {
  const migration = await read('database/migrations/0004_sales_draft_ledger_posting.sql');
  const role = await read('database/tests/prepare-runtime-role.sql');
  const ci = await read('.github/workflows/ci.yml');
  const endpoint = await read('src/server/BusinessERP.Api/Endpoints/SalesDraftLedgerPostingEndpoints.cs');
  const program = await read('src/server/BusinessERP.Api/Program.cs');
  assert.match(migration, /ACCOUNTING_POSTED/);
  assert.match(migration, /fk_sales_invoice_draft_posted_journal/);
  assert.match(migration, /guard_sales_invoice_draft_header_mutation/);
  assert.match(migration, /guard_sales_invoice_draft_line_mutation/);
  assert.match(migration, /linked_source_type <> 'SALES_INVOICE_DRAFT'/);
  assert.match(role, /accounting_posted_journal_entry_id/);
  assert.match(ci, /database\/migrations\/0004_sales_draft_ledger_posting\.sql/);
  assert.match(endpoint, /sales-invoice-drafts\/\{invoiceId:guid\}\/post-to-ledger/);
  assert.match(endpoint, /SALES_INVOICE_DRAFT/);
  assert.match(endpoint, /Status = "APPROVED"/);
  assert.match(endpoint, /Status = "POSTED"/);
  assert.match(endpoint, /sales_draft_vat_not_calculated/);
  assert.match(program, /MapSalesDraftLedgerPostingEndpoints/);
});


test('WPF Ribbon commands are wired to real sales/search/navigation workflows', async () => {
  const app = await read('apps/BusinessERP.Desktop/App.xaml');
  const shell = await read('apps/BusinessERP.Desktop/MainWindow.xaml');
  const code = await read('apps/BusinessERP.Desktop/MainWindow.xaml.cs');
  const salesView = await read('apps/BusinessERP.Desktop/Views/SalesWorkspaceControl.xaml');
  const salesCode = await read('apps/BusinessERP.Desktop/Views/SalesWorkspaceControl.xaml.cs');
  const viewModel = await read('apps/BusinessERP.Desktop/ViewModels/SalesWorkspaceViewModel.cs');
  const apiClient = await read('apps/BusinessERP.Desktop/Services/ErpApiClient.cs');

  assert.match(app, /RibbonTabButtonStyle/);
  assert.match(shell, /x:Name="RibbonActionsPanel"/);
  assert.match(shell, /Click="RibbonNew_Click"/);
  assert.match(shell, /Click="RibbonRefresh_Click"/);
  assert.match(shell, /Click="RibbonSave_Click"/);
  assert.match(shell, /Click="RibbonPostSalesDraft_Click"/);
  assert.match(shell, /Click="ToggleRibbon_Click"/);
  assert.match(shell, /Click="ToggleNavigation_Click"/);
  assert.match(shell, /KeyDown="GeneralSearchBox_KeyDown"/);
  assert.match(shell, /x:Name="RibbonExportExcelButton"/);
  assert.match(shell, /Click="RibbonExportExcel_Click"/);
  assert.match(shell, /Content="PDF" IsEnabled="False" ToolTip="تصدير PDF غير منفذ/);
  assert.match(code, /private async Task RunGeneralSearchAsync/);
  assert.match(code, /private void UpdateRibbonContext/);
  assert.match(salesView, /SelectedItem="{Binding SelectedDraft, Mode=TwoWay}"/);
  assert.match(salesView, /PostSelectedDraftToLedgerCommand/);
  assert.match(salesCode, /SaveCurrentDraftAsync/);
  assert.match(salesCode, /PostSelectedDraftToLedgerAsync/);
  assert.match(viewModel, /SearchLoadedData/);
  assert.match(viewModel, /PrepareNewDraft/);
  assert.match(viewModel, /CanPostSelectedDraft/);
  assert.match(apiClient, /GetFiscalPeriodsAsync/);
  assert.match(apiClient, /PostSalesDraftToLedgerAsync/);
});

test('WPF workspace tables support filtering, sorting, virtualization and keyboard shortcuts', async () => {
  const shell = await read('apps/BusinessERP.Desktop/MainWindow.xaml');
  const code = await read('apps/BusinessERP.Desktop/MainWindow.xaml.cs');
  const view = await read('apps/BusinessERP.Desktop/Views/SalesWorkspaceControl.xaml');
  const vm = await read('apps/BusinessERP.Desktop/ViewModels/SalesWorkspaceViewModel.cs');
  assert.match(shell, /x:Name="OverviewWorkspaceTab"/);
  assert.match(shell, /x:Name="SalesWorkspaceTab"/);
  assert.match(shell, /x:Name="ReportsWorkspaceTab"/);
  assert.match(shell, /PreviewKeyDown="MainWindow_PreviewKeyDown"/);
  assert.match(code, /case Key\.F:/);
  assert.match(code, /case Key\.N:/);
  assert.match(code, /case Key\.R:/);
  assert.match(code, /case Key\.S when/);
  assert.match(view, /ItemsSource="{Binding CustomersView}"/);
  assert.match(view, /ItemsSource="{Binding DraftsView}"/);
  assert.match(view, /CanUserSortColumns/);
  assert.match(view, /VirtualizationMode" Value="Recycling"/);
  assert.match(vm, /CustomersView\.Filter = FilterCustomer/);
  assert.match(vm, /DraftsView\.Filter = FilterDraft/);
});


test('WPF financial reports tab loads real posted-ledger reports from API', async () => {
  const client = await read('apps/BusinessERP.Desktop/Services/ErpApiClient.cs');
  const vm = await read('apps/BusinessERP.Desktop/ViewModels/ReportsWorkspaceViewModel.cs');
  const view = await read('apps/BusinessERP.Desktop/Views/ReportsWorkspaceControl.xaml');
  const shell = await read('apps/BusinessERP.Desktop/MainWindow.xaml');
  const code = await read('apps/BusinessERP.Desktop/MainWindow.xaml.cs');
  assert.match(client, /GetTrialBalanceAsync/);
  assert.match(client, /GetIncomeStatementAsync/);
  assert.match(client, /GetBalanceSheetAsync/);
  assert.match(vm, /Task\.WhenAll\(trialTask, incomeTask, balanceTask\)/);
  assert.match(vm, /POSTED/);
  assert.match(view, /ItemsSource="{Binding TrialBalanceRows}"/);
  assert.match(view, /ItemsSource="{Binding IncomeRows}"/);
  assert.match(view, /ItemsSource="{Binding BalanceRows}"/);
  assert.match(shell, /x:Name="ReportsWorkspacePanel"/);
  assert.match(code, /ReportsWorkspacePanel\.SetApiClientAsync/);
});


test('WPF status bar displays current company and fiscal/report period context', async () => {
  const shell = await read('apps/BusinessERP.Desktop/MainWindow.xaml');
  const mainCode = await read('apps/BusinessERP.Desktop/MainWindow.xaml.cs');
  const salesVm = await read('apps/BusinessERP.Desktop/ViewModels/SalesWorkspaceViewModel.cs');
  const salesControl = await read('apps/BusinessERP.Desktop/Views/SalesWorkspaceControl.xaml.cs');
  const reportsVm = await read('apps/BusinessERP.Desktop/ViewModels/ReportsWorkspaceViewModel.cs');
  const reportsControl = await read('apps/BusinessERP.Desktop/Views/ReportsWorkspaceControl.xaml.cs');
  assert.match(shell, /x:Name="WorkspaceContextText"/);
  assert.match(mainCode, /private void UpdateWorkspaceContext/);
  assert.match(mainCode, /SalesWorkspacePanel\.WorkspaceContextChanged/);
  assert.match(mainCode, /ReportsWorkspacePanel\.WorkspaceContextChanged/);
  assert.match(salesVm, /SelectedFiscalPeriod\?\.DisplayName/);
  assert.match(salesControl, /WorkspaceContextChanged/);
  assert.match(reportsVm, /FromDate:yyyy-MM-dd/);
  assert.match(reportsVm, /AsOfDate:yyyy-MM-dd/);
  assert.match(reportsControl, /WorkspaceContextChanged/);
});


test('financial report Excel export builds a real XLSX workbook with numeric cells and no formulas', async () => {
  const view = await read('apps/BusinessERP.Desktop/Views/ReportsWorkspaceControl.xaml');
  const code = await read('apps/BusinessERP.Desktop/Views/ReportsWorkspaceControl.xaml.cs');
  const shell = await read('apps/BusinessERP.Desktop/MainWindow.xaml');
  const main = await read('apps/BusinessERP.Desktop/MainWindow.xaml.cs');
  const vm = await read('apps/BusinessERP.Desktop/ViewModels/ReportsWorkspaceViewModel.cs');
  assert.match(shell, /x:Name="RibbonExportExcelButton"/);
  assert.match(shell, /Click="RibbonExportExcel_Click"/);
  assert.match(main, /RibbonExportExcelButton\.IsEnabled = _erpApiClient is not null/);
  assert.match(main, /ReportsWorkspacePanel\.ExportCurrentReportToExcel\(\)/);
  assert.match(view, /x:Name="ReportsTabs"/);
  assert.match(code, /SaveFileDialog/);
  assert.match(code, /Excel Workbook \(\*\.xlsx\)/);
  assert.match(code, /ZipArchive/);
  assert.match(code, /xl\/worksheets\/sheet1\.xml/);
  assert.match(code, /WriteString\(Convert\.ToString\(value, CultureInfo\.InvariantCulture\)/);
  assert.match(vm, /CurrentTrialBalance/);
  assert.match(vm, /CurrentIncomeStatement/);
  assert.match(vm, /CurrentBalanceSheet/);
});
