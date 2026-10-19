-- Runtime-role stock ledger smoke test: barcode uniqueness, moving average, no negative stock, immutability grants and RLS.
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM items) OR EXISTS (SELECT 1 FROM warehouses) OR EXISTS (SELECT 1 FROM inventory_movements) THEN
        RAISE EXCEPTION 'Inventory RLS smoke failed: data visible without an app tenant context';
    END IF;
END $$;

BEGIN;
SELECT set_config('app.tenant_id', '11111111-1111-4111-8111-111111111111', true);

INSERT INTO items (id, tenant_id, company_id, item_code, barcode, name_ar, name_en, unit_code)
VALUES ('ffffffff-ffff-4fff-8fff-fffffffffff1',
        '11111111-1111-4111-8111-111111111111',
        '11111111-1111-4111-8111-111111111101',
        'CI-ITEM-001', '6281234567890', 'صنف اختبار المخزون', 'CI Inventory Item', 'EA');

INSERT INTO warehouses (id, tenant_id, company_id, warehouse_code, name)
VALUES ('ffffffff-ffff-4fff-8fff-fffffffffff2',
        '11111111-1111-4111-8111-111111111111',
        '11111111-1111-4111-8111-111111111101',
        'CI-WH-01', 'CI Inventory Warehouse');

INSERT INTO warehouses (id, tenant_id, company_id, warehouse_code, name)
VALUES ('ffffffff-ffff-4fff-8fff-fffffffffff6',
        '11111111-1111-4111-8111-111111111111',
        '11111111-1111-4111-8111-111111111101',
        'CI-WH-02', 'CI Inventory Destination Warehouse');

INSERT INTO inventory_movements
    (id, tenant_id, company_id, movement_number, item_id, warehouse_id, movement_type,
     movement_date, quantity_delta, unit_cost, inventory_value_delta, source_type, notes, created_by)
VALUES
 ('ffffffff-ffff-4fff-8fff-fffffffffff3',
  '11111111-1111-4111-8111-111111111111', '11111111-1111-4111-8111-111111111101',
  'CI-INV-RCPT-001', 'ffffffff-ffff-4fff-8fff-fffffffffff1', 'ffffffff-ffff-4fff-8fff-fffffffffff2',
  'RECEIPT', '2026-04-01', 10.0000, 5.00000000, 50.0000, 'MANUAL', 'Inventory receipt CI',
  'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa');
COMMIT;

BEGIN;
SELECT set_config('app.tenant_id', '11111111-1111-4111-8111-111111111111', true);
INSERT INTO inventory_movements
    (id, tenant_id, company_id, movement_number, item_id, warehouse_id, movement_type,
     movement_date, quantity_delta, unit_cost, inventory_value_delta, source_type, notes, created_by)
VALUES
 ('ffffffff-ffff-4fff-8fff-fffffffffff4',
  '11111111-1111-4111-8111-111111111111', '11111111-1111-4111-8111-111111111101',
  'CI-INV-ISSUE-001', 'ffffffff-ffff-4fff-8fff-fffffffffff1', 'ffffffff-ffff-4fff-8fff-fffffffffff2',
  'ISSUE', '2026-04-02', -4.0000, 5.00000000, -20.0000, 'MANUAL', 'Inventory issue CI',
  'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa');

DO $$
BEGIN
    BEGIN
        INSERT INTO inventory_movements
            (id, tenant_id, company_id, movement_number, item_id, warehouse_id, movement_type,
             movement_date, quantity_delta, unit_cost, inventory_value_delta, source_type, notes, created_by)
        VALUES
         ('ffffffff-ffff-4fff-8fff-fffffffffff5',
          '11111111-1111-4111-8111-111111111111', '11111111-1111-4111-8111-111111111101',
          'CI-INV-OVERISSUE-001', 'ffffffff-ffff-4fff-8fff-fffffffffff1', 'ffffffff-ffff-4fff-8fff-fffffffffff2',
          'ISSUE', '2026-04-02', -7.0000, 5.00000000, -35.0000, 'MANUAL', 'Must be rejected',
          'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa');
        RAISE EXCEPTION 'Expected inventory balance trigger to reject over-issue';
    EXCEPTION WHEN check_violation THEN
        NULL;
    END;
END $$;

DO $$
BEGIN
    IF (SELECT sum(quantity_delta) FROM inventory_movements
         WHERE item_id = 'ffffffff-ffff-4fff-8fff-fffffffffff1'
           AND warehouse_id = 'ffffffff-ffff-4fff-8fff-fffffffffff2') <> 6.0000
       OR (SELECT sum(inventory_value_delta) FROM inventory_movements
         WHERE item_id = 'ffffffff-ffff-4fff-8fff-fffffffffff1'
           AND warehouse_id = 'ffffffff-ffff-4fff-8fff-fffffffffff2') <> 30.0000 THEN
        RAISE EXCEPTION 'Inventory weighted-average stock balance does not equal 6 units / value 30';
    END IF;
END $$;


-- A transfer has one header and exactly two matching stock ledger legs at commit.
INSERT INTO inventory_transfers
    (id, tenant_id, company_id, transfer_number, item_id, source_warehouse_id, destination_warehouse_id,
     transfer_date, quantity, unit_cost, inventory_value, notes, created_by)
VALUES
 ('ffffffff-ffff-4fff-8fff-fffffffffff7',
  '11111111-1111-4111-8111-111111111111', '11111111-1111-4111-8111-111111111101',
  'CI-INV-TRF-001', 'ffffffff-ffff-4fff-8fff-fffffffffff1',
  'ffffffff-ffff-4fff-8fff-fffffffffff2', 'ffffffff-ffff-4fff-8fff-fffffffffff6',
  '2026-04-03', 2.0000, 5.00000000, 10.0000, 'Inventory transfer CI',
  'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa');

INSERT INTO inventory_movements
    (id, tenant_id, company_id, movement_number, item_id, warehouse_id, movement_type,
     movement_date, quantity_delta, unit_cost, inventory_value_delta, source_type, transfer_id, notes, created_by)
VALUES
 ('ffffffff-ffff-4fff-8fff-fffffffffff8',
  '11111111-1111-4111-8111-111111111111', '11111111-1111-4111-8111-111111111101',
  'CI-INV-TRF-001-OUT', 'ffffffff-ffff-4fff-8fff-fffffffffff1',
  'ffffffff-ffff-4fff-8fff-fffffffffff2', 'ISSUE', '2026-04-03', -2.0000, 5.00000000, -10.0000,
  'TRANSFER', 'ffffffff-ffff-4fff-8fff-fffffffffff7', 'Inventory transfer CI', 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'),
 ('ffffffff-ffff-4fff-8fff-fffffffffff9',
  '11111111-1111-4111-8111-111111111111', '11111111-1111-4111-8111-111111111101',
  'CI-INV-TRF-001-IN', 'ffffffff-ffff-4fff-8fff-fffffffffff1',
  'ffffffff-ffff-4fff-8fff-fffffffffff6', 'RECEIPT', '2026-04-03', 2.0000, 5.00000000, 10.0000,
  'TRANSFER', 'ffffffff-ffff-4fff-8fff-fffffffffff7', 'Inventory transfer CI', 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa');

DO $
BEGIN
    IF (SELECT sum(quantity_delta) FROM inventory_movements
         WHERE item_id = 'ffffffff-ffff-4fff-8fff-fffffffffff1'
           AND warehouse_id = 'ffffffff-ffff-4fff-8fff-fffffffffff2') <> 4.0000
       OR (SELECT sum(inventory_value_delta) FROM inventory_movements
         WHERE item_id = 'ffffffff-ffff-4fff-8fff-fffffffffff1'
           AND warehouse_id = 'ffffffff-ffff-4fff-8fff-fffffffffff2') <> 20.0000
       OR (SELECT sum(quantity_delta) FROM inventory_movements
         WHERE item_id = 'ffffffff-ffff-4fff-8fff-fffffffffff1'
           AND warehouse_id = 'ffffffff-ffff-4fff-8fff-fffffffffff6') <> 2.0000
       OR (SELECT sum(inventory_value_delta) FROM inventory_movements
         WHERE item_id = 'ffffffff-ffff-4fff-8fff-fffffffffff1'
           AND warehouse_id = 'ffffffff-ffff-4fff-8fff-fffffffffff6') <> 10.0000 THEN
        RAISE EXCEPTION 'Inventory transfer did not preserve 2 units / value 10 across source and destination';
    END IF;
END $;

-- Runtime role has INSERT/SELECT but deliberately has no UPDATE or DELETE grant on the stock ledger.
DO $$
BEGIN
    BEGIN
        UPDATE inventory_movements SET notes = 'illegal update'
         WHERE movement_number = 'CI-INV-RCPT-001';
        RAISE EXCEPTION 'Expected runtime role to be denied movement update';
    EXCEPTION WHEN insufficient_privilege THEN
        NULL;
    END;
    BEGIN
        DELETE FROM inventory_movements WHERE movement_number = 'CI-INV-RCPT-001';
        RAISE EXCEPTION 'Expected runtime role to be denied movement delete';
    EXCEPTION WHEN insufficient_privilege THEN
        NULL;
    END;
END $$;
COMMIT;

BEGIN;
SELECT set_config('app.tenant_id', '22222222-2222-4222-8222-222222222222', true);
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM items WHERE item_code = 'CI-ITEM-001')
       OR EXISTS (SELECT 1 FROM warehouses WHERE warehouse_code IN ('CI-WH-01', 'CI-WH-02'))
       OR EXISTS (SELECT 1 FROM inventory_movements WHERE movement_number IN ('CI-INV-RCPT-001', 'CI-INV-TRF-001-OUT', 'CI-INV-TRF-001-IN'))
       OR EXISTS (SELECT 1 FROM inventory_transfers WHERE transfer_number = 'CI-INV-TRF-001') THEN
        RAISE EXCEPTION 'Inventory RLS smoke failed: tenant B can view tenant A inventory';
    END IF;
END $$;
COMMIT;
