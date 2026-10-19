import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { resolve } from 'node:path';

const root = resolve(import.meta.dirname, '../..');
const read = (path) => readFile(resolve(root, path), 'utf8');

test('purchase draft ledger posting creates a tenant-scoped idempotent balanced journal and locks source data', async () => {
  const migration = await read('database/migrations/0006_purchase_draft_ledger_posting.sql');
  const endpoint = await read('src/server/BusinessERP.Api/Endpoints/PurchaseDraftLedgerPostingEndpoints.cs');
  const program = await read('src/server/BusinessERP.Api/Program.cs');
  const purchaseApi = await read('src/server/BusinessERP.Api/Endpoints/PurchaseInvoiceDraftEndpoints.cs');
  const runtimeGrants = await read('database/tests/prepare-runtime-role.sql');
  const smoke = await read('database/tests/purchase-draft-smoke.sql');
  const ci = await read('.github/workflows/ci.yml');

  assert.match(migration, /accounting_posted_journal_entry_id/);
  assert.match(migration, /accounting_input_vat_account_id/);
  assert.match(migration, /REFERENCES journal_entries\(tenant_id, company_id, id\)/);
  assert.match(migration, /REFERENCES accounts\(tenant_id, company_id, id\)/);
  assert.match(migration, /guard_purchase_invoice_draft_header_mutation/);
  assert.match(migration, /guard_purchase_invoice_draft_line_mutation/);
  assert.match(migration, /ACCOUNTING_POSTED/);
  assert.match(migration, /OLD\.status = 'ACCOUNTING_POSTED'/);
  assert.match(migration, /linked_source_type IS DISTINCT FROM 'PURCHASE_INVOICE_DRAFT'/);
  assert.match(migration, /ux_purchase_invoice_drafts_posted_journal/);

  assert.match(endpoint, /post-to-ledger/);
  assert.match(endpoint, /purchase-draft-ledger-posting:/);
  assert.match(endpoint, /x\.SourceType == "PURCHASE_INVOICE_DRAFT" && x\.SourceId == invoiceId/);
  assert.match(endpoint, /alreadyPosted: true/);
  assert.match(endpoint, /FiscalPeriodId/);
  assert.match(endpoint, /period\.Status != "OPEN"/);
  assert.match(endpoint, /AccountType == "ASSET" \|\| account\.AccountType == "EXPENSE"/);
  assert.match(endpoint, /IsPostable\(payable, "LIABILITY", "CREDIT"\)/);
  assert.match(endpoint, /INCLUDED_IN_PURCHASE_COST/);
  assert.match(endpoint, /ASSET_ACCOUNT_SELECTED_BY_USER/);
  assert.match(endpoint, /PURCHASE_DRAFT_POSTED_TO_GL/);
  assert.match(endpoint, /JournalEntryLines\.Add/);
  assert.match(program, /MapPurchaseDraftLedgerPostingEndpoints/);

  assert.match(purchaseApi, /accounting_posted_journal_entry_id AS "AccountingPostedJournalEntryId"/);
  assert.match(purchaseApi, /accounting_input_vat_account_id AS "AccountingInputVatAccountId"/);
  assert.match(runtimeGrants, /GRANT UPDATE \([\s\S]*accounting_posted_journal_entry_id[\s\S]*accounting_input_vat_account_id[\s\S]*\) ON purchase_invoice_drafts/);
  assert.match(smoke, /CI-PUR-GL-0001/);
  assert.match(smoke, /Expected posted purchase header to be immutable/);
  assert.match(smoke, /Expected posted purchase line to be immutable/);
  assert.match(smoke, /COALESCE\(sum\(debit\), 0\)/);
  assert.match(ci, /database\/migrations\/0006_purchase_draft_ledger_posting\.sql/);
  assert.match(ci, /database\/tests\/purchase-draft-smoke\.sql/);
  assert.equal((ci.match(/database\/tests\/purchase-draft-smoke\.sql/g) ?? []).length, 1,
    'The stateful purchase smoke test must run exactly once after migrations and runtime-role setup');
});

test('desktop UI selects a valid posting period and accounts before posting a purchase draft', async () => {
  const client = await read('apps/BusinessERP.Desktop/Services/ErpApiClient.cs');
  const viewModel = await read('apps/BusinessERP.Desktop/ViewModels/PurchasesWorkspaceViewModel.cs');
  const view = await read('apps/BusinessERP.Desktop/Views/PurchasesWorkspaceControl.xaml');

  assert.match(client, /GetAccountsAsync/);
  assert.match(client, /GetFiscalPeriodsAsync/);
  assert.match(client, /PostPurchaseDraftToLedgerAsync/);
  assert.match(client, /AccountingPostedJournalEntryId/);
  assert.match(viewModel, /PostSelectedDraftToLedgerCommand/);
  assert.match(viewModel, /CanPostSelectedDraftToLedger/);
  assert.match(viewModel, /SelectedDraftDetails\.Status == "DRAFT"/);
  assert.match(viewModel, /period\.Status == "OPEN"/);
  assert.match(viewModel, /PurchaseDebitAccounts/);
  assert.match(viewModel, /PayableAccounts/);
  assert.match(viewModel, /InputVatAccounts/);
  assert.match(viewModel, /PostSelectedDraftToLedgerAsync/);
  assert.match(view, /SelectedFiscalPeriod/);
  assert.match(view, /SelectedPurchaseDebitAccount/);
  assert.match(view, /SelectedPayableAccount/);
  assert.match(view, /SelectedInputVatAccount/);
  assert.match(view, /PostSelectedDraftToLedgerCommand/);
  assert.match(view, /IsReadOnly="{Binding IsSelectedDraftPosted}"/);
});
