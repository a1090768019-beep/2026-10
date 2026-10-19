import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { resolve } from 'node:path';

const root = resolve(import.meta.dirname, '../..');
const read = (path) => readFile(resolve(root, path), 'utf8');

test('inventory catalog and stock ledger are tenant-scoped, barcode-unique and immutable', async () => {
  const migration = await read('database/migrations/0007_inventory_stock_ledger.sql');
  const endpoint = await read('src/server/BusinessERP.Api/Endpoints/InventoryEndpoints.cs');
  const runtimeGrants = await read('database/tests/prepare-runtime-role.sql');
  const smoke = await read('database/tests/inventory-stock-smoke.sql');
  const ci = await read('.github/workflows/ci.yml');
  const program = await read('src/server/BusinessERP.Api/Program.cs');

  assert.match(migration, /CREATE TABLE items/);
  assert.match(migration, /CREATE TABLE warehouses/);
  assert.match(migration, /CREATE TABLE inventory_movements/);
  assert.match(migration, /ux_items_tenant_company_barcode/);
  assert.match(migration, /ENABLE ROW LEVEL SECURITY/);
  assert.match(migration, /FORCE ROW LEVEL SECURITY/);
  assert.match(migration, /prevent_inventory_movement_mutation/);
  assert.match(migration, /guard_inventory_movement_balance/);
  assert.match(migration, /pg_advisory_xact_lock/);
  assert.match(migration, /NEW\.movement_type = 'ISSUE'/);
  assert.match(migration, /Inventory issue would make on-hand quantity negative/);

  assert.match(program, /MapInventoryEndpoints/);
  assert.match(endpoint, /MapPost\("\/items", CreateItem\)/);
  assert.match(endpoint, /MapPost\("\/warehouses", CreateWarehouse\)/);
  assert.match(endpoint, /\/inventory\/receipts/);
  assert.match(endpoint, /\/inventory\/issues/);
  assert.match(endpoint, /movement-number:/);
  assert.match(endpoint, /inventory-balance:/);
  assert.match(endpoint, /insufficient_stock/);
  assert.match(endpoint, /sum\(movement\.inventory_value_delta\)/);
  assert.match(endpoint, /INVENTORY_RECEIPT_RECORDED/);
  assert.match(endpoint, /INVENTORY_ISSUE_RECORDED/);
  assert.match(runtimeGrants, /GRANT SELECT, INSERT ON items, warehouses, inventory_movements/);
  assert.match(smoke, /CI-INV-RCPT-001/);
  assert.match(smoke, /CI-INV-ISSUE-001/);
  assert.match(smoke, /Expected inventory balance trigger to reject over-issue/);
  assert.match(smoke, /tenant B can view tenant A inventory/);
  assert.match(ci, /database\/migrations\/0007_inventory_stock_ledger\.sql/);
  assert.match(ci, /database\/tests\/inventory-stock-smoke\.sql/);
  assert.equal((ci.match(/database\/tests\/inventory-stock-smoke\.sql/g) ?? []).length, 1);
});

test('WPF inventory workspace exposes barcode master data, stock receipts/issues and stock card reports', async () => {
  const client = await read('apps/BusinessERP.Desktop/Services/ErpApiClient.cs');
  const viewModel = await read('apps/BusinessERP.Desktop/ViewModels/InventoryWorkspaceViewModel.cs');
  const view = await read('apps/BusinessERP.Desktop/Views/InventoryWorkspaceControl.xaml');
  const codeBehind = await read('apps/BusinessERP.Desktop/Views/InventoryWorkspaceControl.xaml.cs');
  const shell = await read('apps/BusinessERP.Desktop/MainWindow.xaml');
  const main = await read('apps/BusinessERP.Desktop/MainWindow.xaml.cs');

  assert.match(client, /GetInventoryItemsAsync/);
  assert.match(client, /CreateInventoryItemAsync/);
  assert.match(client, /GetInventoryWarehousesAsync/);
  assert.match(client, /ReceiveInventoryStockAsync/);
  assert.match(client, /IssueInventoryStockAsync/);
  assert.match(client, /GetInventoryBalancesAsync/);
  assert.match(client, /GetInventoryMovementsAsync/);
  assert.match(viewModel, /CreateItemCommand/);
  assert.match(viewModel, /CreateWarehouseCommand/);
  assert.match(viewModel, /ReceiveStockCommand/);
  assert.match(viewModel, /IssueStockCommand/);
  assert.match(viewModel, /RefreshStockCardCommand/);
  assert.match(view, /Binding NewBarcode/);
  assert.match(view, /ItemsSource="{Binding StockBalances}"/);
  assert.match(view, /ItemsSource="{Binding Movements}"/);
  assert.match(view, /ReceiveStockCommand/);
  assert.match(view, /IssueStockCommand/);
  assert.match(codeBehind, /InventoryWorkspaceViewModel/);
  assert.match(shell, /x:Name="InventoryWorkspaceTab"/);
  assert.match(shell, /x:Name="InventoryWorkspacePanel"/);
  assert.match(main, /string\.Equals\(key, "Inventory", StringComparison\.OrdinalIgnoreCase\)/);
  assert.match(main, /InventoryWorkspacePanel\.SetApiClientAsync/);
});

test('inventory balance and movement reports export to real Excel workbooks with current-page limits explained', async () => {
  const view = await read('apps/BusinessERP.Desktop/Views/InventoryWorkspaceControl.xaml');
  const code = await read('apps/BusinessERP.Desktop/Views/InventoryWorkspaceControl.xaml.cs');
  const reportWriter = await read('apps/BusinessERP.Desktop/Views/ReportsWorkspaceControl.xaml.cs');

  assert.match(view, /Click="ExportStockBalances_Click"/);
  assert.match(view, /Click="ExportInventoryMovements_Click"/);
  assert.match(code, /private void ExportStockBalances_Click/);
  assert.match(code, /private void ExportInventoryMovements_Click/);
  assert.match(code, /MinimalXlsxWriter\.Write/);
  assert.match(code, /InventoryBalances/);
  assert.match(code, /InventoryMovements/);
  assert.match(code, /الصفحة المحمّلة حاليًا فقط/);
  assert.match(reportWriter, /internal static class MinimalXlsxWriter/);
});
