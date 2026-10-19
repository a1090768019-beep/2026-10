import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { resolve } from 'node:path';

const root = resolve(import.meta.dirname, '../..');
const read = (path) => readFile(resolve(root, path), 'utf8');

test('receivables and payables aging reports use ledger posting dates and enforce tenant/page boundaries', async () => {
  const endpoint = await read('src/server/BusinessERP.Api/Endpoints/ReceivablesPayablesAgingEndpoints.cs');
  const program = await read('src/server/BusinessERP.Api/Program.cs');
  const client = await read('apps/BusinessERP.Desktop/Services/ErpApiClient.cs');
  const viewModel = await read('apps/BusinessERP.Desktop/ViewModels/SettlementsWorkspaceViewModel.cs');
  const view = await read('apps/BusinessERP.Desktop/Views/SettlementsWorkspaceControl.xaml');

  assert.ok(endpoint.includes('/reports/receivables-aging'));
  assert.ok(endpoint.includes('/reports/payables-aging'));
  assert.match(endpoint, /AllowedRoles\.Contains\(tenant\.RoleCode/);
  assert.equal((endpoint.match(/invoice_je\.entry_date <= \{reportDate\}/g) || []).length, 4);
  assert.equal((endpoint.match(/settlement_je\.entry_date <= \{reportDate\}/g) || []).length, 4);
  assert.match(endpoint, /i\.status='ACCOUNTING_POSTED' AND i\.issue_date <= \{reportDate\}/);
  assert.match(endpoint, /pageSize is < 1 or > 200/);
  assert.match(endpoint, /LIMIT \{pageSize\} OFFSET \{offset\}/);
  for (const bucket of ['CURRENT', 'DAYS_1_30', 'DAYS_31_60', 'DAYS_61_90', 'OVER_90']) {
    assert.ok(endpoint.includes("'" + bucket + "'"), 'Missing aging bucket: ' + bucket);
  }
  assert.match(program, /MapReceivablesPayablesAgingEndpoints/);
  assert.match(client, /GetReceivablesAgingAsync/);
  assert.match(client, /GetPayablesAgingAsync/);
  assert.match(viewModel, /RefreshAgingReportsCommand/);
  assert.match(viewModel, /PreviousReceivablesAgingPageCommand/);
  assert.match(viewModel, /NextPayablesAgingPageCommand/);
  assert.match(view, /ItemsSource="\{Binding ReceivablesAgingRows\}"/);
  assert.match(view, /ItemsSource="\{Binding PayablesAgingRows\}"/);
  assert.match(view, /Binding AgingBucketLabel/);
});
