-- Verify one-time double-entry posting links for valued manual inventory movements.
BEGIN;
SELECT set_config('app.tenant_id', '11111111-1111-4111-8111-111111111111', true);

-- Use movements created by this test; the preceding stock smoke deliberately deletes its fixtures.
INSERT INTO warehouses (id, tenant_id, company_id, warehouse_code, name)
VALUES ('eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee6',
        '11111111-1111-4111-8111-111111111111',
        '11111111-1111-4111-8111-111111111101',
        'CI-GL-WH', 'CI GL Posting Warehouse');

INSERT INTO items (id, tenant_id, company_id, item_code, barcode, name_ar, name_en, unit_code, tracks_serials)
VALUES ('eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee5',
        '11111111-1111-4111-8111-111111111111',
        '11111111-1111-4111-8111-111111111101',
        'CI-GL-ITEM', 'CI-GL-BARCODE', 'صنف ترحيل مخزني', 'CI GL Posting Item', 'EA', false);

INSERT INTO inventory_movements
 (id, tenant_id, company_id, movement_number, item_id, warehouse_id, movement_type, movement_date,
  quantity_delta, unit_cost, inventory_value_delta, source_type, transfer_id, notes, created_by)
VALUES
 ('eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee7',
  '11111111-1111-4111-8111-111111111111','11111111-1111-4111-8111-111111111101',
  'CI-INV-GL-MOV-RCPT','eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee5','eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee6',
  'RECEIPT','2026-04-01',5.0000,10.00000000,50.0000,'MANUAL',NULL,'inventory accounting smoke receipt',
  'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'),
 ('eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee8',
  '11111111-1111-4111-8111-111111111111','11111111-1111-4111-8111-111111111101',
  'CI-INV-GL-MOV-ISSUE','eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee5','eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee6',
  'ISSUE','2026-04-02',-2.0000,10.00000000,-20.0000,'MANUAL',NULL,'inventory accounting smoke issue',
  'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa');

INSERT INTO journal_entries
 (id,tenant_id,company_id,fiscal_period_id,entry_number,entry_date,description,currency_code,status,source_type,source_id,created_at)
VALUES ('eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee1','11111111-1111-4111-8111-111111111111','11111111-1111-4111-8111-111111111101',
 '11111111-1111-4111-8111-111111111103','CI-INV-GL-RCPT-001','2026-04-01','Inventory receipt GL smoke','SAR','DRAFT',
 'INVENTORY_MOVEMENT','eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee7',now());
INSERT INTO journal_entry_lines(tenant_id,company_id,journal_entry_id,account_id,line_number,debit,credit) VALUES
 ('11111111-1111-4111-8111-111111111111','11111111-1111-4111-8111-111111111101','eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee1','11111111-1111-4111-8111-111111111110',1,50.0000,0),
 ('11111111-1111-4111-8111-111111111111','11111111-1111-4111-8111-111111111101','eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee1','11111111-1111-4111-8111-111111111105',2,0,50.0000);
UPDATE journal_entries SET status='APPROVED' WHERE id='eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee1';
UPDATE journal_entries SET status='POSTED',posted_at=now() WHERE id='eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee1';
INSERT INTO inventory_movement_accounting_postings
 (id,tenant_id,company_id,inventory_movement_id,journal_entry_id,inventory_asset_account_id,counterpart_account_id,movement_type,amount,posted_by)
VALUES ('eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee2','11111111-1111-4111-8111-111111111111','11111111-1111-4111-8111-111111111101',
 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee7','eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee1','11111111-1111-4111-8111-111111111110',
 '11111111-1111-4111-8111-111111111105','RECEIPT',50.0000,'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa');

INSERT INTO journal_entries
 (id,tenant_id,company_id,fiscal_period_id,entry_number,entry_date,description,currency_code,status,source_type,source_id,created_at)
VALUES ('eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee3','11111111-1111-4111-8111-111111111111','11111111-1111-4111-8111-111111111101',
 '11111111-1111-4111-8111-111111111103','CI-INV-GL-ISSUE-001','2026-04-02','Inventory issue GL smoke','SAR','DRAFT',
 'INVENTORY_MOVEMENT','eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee8',now());
INSERT INTO journal_entry_lines(tenant_id,company_id,journal_entry_id,account_id,line_number,debit,credit) VALUES
 ('11111111-1111-4111-8111-111111111111','11111111-1111-4111-8111-111111111101','eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee3','11111111-1111-4111-8111-111111111111',1,20.0000,0),
 ('11111111-1111-4111-8111-111111111111','11111111-1111-4111-8111-111111111101','eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee3','11111111-1111-4111-8111-111111111110',2,0,20.0000);
UPDATE journal_entries SET status='APPROVED' WHERE id='eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee3';
UPDATE journal_entries SET status='POSTED',posted_at=now() WHERE id='eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee3';
INSERT INTO inventory_movement_accounting_postings
 (id,tenant_id,company_id,inventory_movement_id,journal_entry_id,inventory_asset_account_id,counterpart_account_id,movement_type,amount,posted_by)
VALUES ('eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee4','11111111-1111-4111-8111-111111111111','11111111-1111-4111-8111-111111111101',
 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee8','eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee3','11111111-1111-4111-8111-111111111110',
 '11111111-1111-4111-8111-111111111111','ISSUE',20.0000,'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa');

DO $$
BEGIN
  IF (SELECT count(*) FROM inventory_movement_accounting_postings
       WHERE company_id='11111111-1111-4111-8111-111111111101') <> 2 THEN
    RAISE EXCEPTION 'Expected receipt and issue accounting links';
  END IF;
  IF (SELECT count(*) FROM journal_entries
       WHERE source_type='INVENTORY_MOVEMENT' AND status='POSTED'
         AND company_id='11111111-1111-4111-8111-111111111101') <> 2 THEN
    RAISE EXCEPTION 'Expected two posted inventory source journals';
  END IF;
  IF (SELECT sum(debit) FROM journal_entry_lines WHERE journal_entry_id='eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee1') <> 50.0000
     OR (SELECT sum(credit) FROM journal_entry_lines WHERE journal_entry_id='eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee1') <> 50.0000
     OR (SELECT sum(debit) FROM journal_entry_lines WHERE journal_entry_id='eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee3') <> 20.0000
     OR (SELECT sum(credit) FROM journal_entry_lines WHERE journal_entry_id='eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee3') <> 20.0000 THEN
    RAISE EXCEPTION 'Inventory accounting journals are not balanced';
  END IF;
END $$;

SELECT set_config('app.tenant_id','22222222-2222-4222-8222-222222222222',true);
DO $$
BEGIN
  IF EXISTS (SELECT 1 FROM inventory_movement_accounting_postings)
     OR EXISTS (SELECT 1 FROM journal_entries WHERE source_type='INVENTORY_MOVEMENT') THEN
    RAISE EXCEPTION 'tenant B can view tenant A inventory accounting postings';
  END IF;
END $$;
SELECT set_config('app.tenant_id','11111111-1111-4111-8111-111111111111',true);
COMMIT;
