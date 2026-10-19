import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { resolve } from 'node:path';

const root = resolve(import.meta.dirname, '../..');
const read = (path) => readFile(resolve(root, path), 'utf8');

test('settlement posting is tenant-scoped, balanced, idempotent, and immutable after posting', async () => {
  const migration = await read('database/migrations/0012_settlement_posting.sql');
  const endpoint = await read('src/server/BusinessERP.Api/Endpoints/SettlementEndpoints.cs');
  const program = await read('src/server/BusinessERP.Api/Program.cs');
  const grants = await read('database/tests/prepare-runtime-role.sql');
  const ci = await read('.github/workflows/ci.yml');

  for (const table of ['customer_receipts', 'customer_receipt_allocations', 'supplier_payments', 'supplier_payment_allocations']) {
    assert.match(migration, new RegExp('CREATE TABLE ' + table));
    assert.match(migration, new RegExp('ENABLE ROW LEVEL SECURITY'));
    assert.match(migration, new RegExp('FORCE ROW LEVEL SECURITY'));
  }
  assert.match(migration, /REFERENCES customers\(tenant_id, company_id, id\)/);
  assert.match(migration, /REFERENCES suppliers\(tenant_id, company_id, id\)/);
  assert.match(migration, /REFERENCES sales_invoice_drafts\(tenant_id, company_id, id\)/);
  assert.match(migration, /REFERENCES purchase_invoice_drafts\(tenant_id, company_id, id\)/);
  assert.match(migration, /guard_customer_receipt_mutation/);
  assert.match(migration, /guard_supplier_payment_mutation/);
  assert.match(migration, /allocations must equal the receipt amount/i);
  assert.match(migration, /allocations must equal the payment amount/i);
  assert.match(migration, /receipt allocation exceeds the invoice remaining balance/i);
  assert.match(migration, /payment allocation exceeds the invoice remaining balance/i);

  for (const route of [
    '/customer-receipts',
    '/customer-receipts/{receiptId:guid}/post-to-ledger',
    '/supplier-payments',
    '/supplier-payments/{paymentId:guid}/post-to-ledger'
  ]) assert.ok(endpoint.includes(route), 'Missing endpoint route: ' + route);
  assert.match(endpoint, /customer-invoice-balance:/);
  assert.match(endpoint, /supplier-invoice-balance:/);
  assert.match(endpoint, /period\.Status != "OPEN"/);
  assert.match(endpoint, /JournalEntryLines\.Add/);
  assert.match(endpoint, /AlreadyPosted/);
  assert.match(endpoint, /tenant\.TenantId/);
  assert.match(endpoint, /AllowedRoles/);
  assert.match(program, /MapSettlementEndpoints/);
  assert.match(grants, /customer_receipts, customer_receipt_allocations,[\s\S]*supplier_payments, supplier_payment_allocations/);
  assert.match(grants, /GRANT UPDATE \(status, accounting_posted_journal_entry_id, accounting_posted_at\)[\s\S]*ON customer_receipts, supplier_payments/);
  assert.match(ci, /database\/migrations\/0012_settlement_posting\.sql/);
  assert.match(ci, /tests\/contracts\/\*\.test\.mjs/);
});
