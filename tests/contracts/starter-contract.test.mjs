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
  assert.match(view, /لا يُحفظان في مسودة البيع/);
  assert.match(view, /DisplayMemberPath="Label"/);
  assert.match(codeBehind, /await _viewModel\.CalculateVatPreviewAsync\(\)/);
});
