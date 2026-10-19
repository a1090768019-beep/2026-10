-- Verify invoice lines are linked one-to-one to stock movements and serial events.
BEGIN;
SELECT set_config('app.tenant_id','11111111-1111-4111-8111-111111111111',true);

INSERT INTO suppliers (id,tenant_id,company_id,supplier_code,name,tax_number,is_active)
VALUES ('99999999-9999-4999-8999-999999999901','11111111-1111-4111-8111-111111111111',
'11111111-1111-4111-8111-111111111101','DOC-LINK-SUPPLIER','Document link supplier','300000000000003',true);
INSERT INTO warehouses (id,tenant_id,company_id,warehouse_code,name)
VALUES ('99999999-9999-4999-8999-999999999902','11111111-1111-4111-8111-111111111111',
'11111111-1111-4111-8111-111111111101','DOC-LINK-WH','Document linked warehouse');
INSERT INTO items (id,tenant_id,company_id,item_code,barcode,name_ar,name_en,unit_code,tracks_serials)
VALUES
('99999999-9999-4999-8999-999999999903','11111111-1111-4111-8111-111111111111','11111111-1111-4111-8111-111111111101','DOC-LINK-STOCK','DOC-LINK-BC-1','صنف عادي','Normal stock item','EA',false),
('99999999-9999-4999-8999-999999999904','11111111-1111-4111-8111-111111111111','11111111-1111-4111-8111-111111111101','DOC-LINK-SERIAL','DOC-LINK-BC-2','صنف متسلسل','Serial stock item','EA',true);

INSERT INTO purchase_invoice_drafts
(id,tenant_id,company_id,supplier_id,purchase_number,supplier_invoice_number,issue_date,due_date,currency_code,
 supplier_name_snapshot,supplier_tax_number_snapshot,subtotal_amount,notes,status,created_by)
VALUES ('99999999-9999-4999-8999-999999999905','11111111-1111-4111-8111-111111111111',
'11111111-1111-4111-8111-111111111101','99999999-9999-4999-8999-999999999901',
'DOC-LINK-PUR-1','DOC-LINK-SUP-INV-1','2026-10-01','2026-10-31','SAR',
'Document link supplier','300000000000003',70.0000,'source movement smoke','DRAFT','aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa');
INSERT INTO purchase_invoice_draft_lines
(id,tenant_id,company_id,purchase_invoice_draft_id,line_number,description,quantity,unit_price,line_amount,
 inventory_item_id,inventory_warehouse_id,serial_numbers,inventory_cost_amount)
VALUES
('99999999-9999-4999-8999-999999999906','11111111-1111-4111-8111-111111111111','11111111-1111-4111-8111-111111111101',
 '99999999-9999-4999-8999-999999999905',1,'Ordinary stock line',5.0000,10.0000,50.0000,
 '99999999-9999-4999-8999-999999999903','99999999-9999-4999-8999-999999999902','[]'::jsonb,50.0000),
('99999999-9999-4999-8999-999999999907','11111111-1111-4111-8111-111111111111','11111111-1111-4111-8111-111111111101',
 '99999999-9999-4999-8999-999999999905',2,'Serial stock line',1.0000,20.0000,20.0000,
 '99999999-9999-4999-8999-999999999904','99999999-9999-4999-8999-999999999902','["DOC-LINK-SN-001"]'::jsonb,20.0000);

INSERT INTO inventory_movements
(id,tenant_id,company_id,movement_number,item_id,warehouse_id,movement_type,movement_date,quantity_delta,unit_cost,
 inventory_value_delta,source_type,transfer_id,purchase_invoice_draft_line_id,sales_invoice_draft_line_id,notes,created_by)
VALUES
('99999999-9999-4999-8999-999999999908','11111111-1111-4111-8111-111111111111','11111111-1111-4111-8111-111111111101',
 'DOC-PUR-RCPT-001','99999999-9999-4999-8999-999999999903','99999999-9999-4999-8999-999999999902','RECEIPT','2026-10-01',5.0000,10.00000000,50.0000,
 'PURCHASE',NULL,'99999999-9999-4999-8999-999999999906',NULL,'linked purchase receipt','aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'),
('99999999-9999-4999-8999-999999999909','11111111-1111-4111-8111-111111111111','11111111-1111-4111-8111-111111111101',
 'DOC-PUR-RCPT-002','99999999-9999-4999-8999-999999999904','99999999-9999-4999-8999-999999999902','RECEIPT','2026-10-01',1.0000,20.00000000,20.0000,
 'PURCHASE',NULL,'99999999-9999-4999-8999-999999999907',NULL,'linked serial purchase receipt','aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa');

INSERT INTO inventory_serial_numbers
(id,tenant_id,company_id,item_id,serial_number,status,current_warehouse_id,first_received_movement_id,last_movement_id)
VALUES ('99999999-9999-4999-8999-999999999910','11111111-1111-4111-8111-111111111111','11111111-1111-4111-8111-111111111101',
'99999999-9999-4999-8999-999999999904','DOC-LINK-SN-001','IN_STOCK','99999999-9999-4999-8999-999999999902',
'99999999-9999-4999-8999-999999999909','99999999-9999-4999-8999-999999999909');
INSERT INTO inventory_serial_events
(id,tenant_id,company_id,serial_id,item_id,movement_id,transfer_id,event_type,warehouse_id,movement_date,created_by)
VALUES ('99999999-9999-4999-8999-999999999911','11111111-1111-4111-8111-111111111111','11111111-1111-4111-8111-111111111101',
'99999999-9999-4999-8999-999999999910','99999999-9999-4999-8999-999999999904','99999999-9999-4999-8999-999999999909',NULL,
'RECEIPT','99999999-9999-4999-8999-999999999902','2026-10-01','aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa');

INSERT INTO sales_invoice_drafts
(id,tenant_id,company_id,customer_id,invoice_number,issue_date,due_date,currency_code,customer_name_snapshot,
 customer_tax_number_snapshot,subtotal_amount,notes,status,created_by)
VALUES ('99999999-9999-4999-8999-999999999912','11111111-1111-4111-8111-111111111111',
'11111111-1111-4111-8111-111111111101','cccccccc-cccc-4ccc-8ccc-ccccccccccc1',
'DOC-LINK-SALES-1','2026-10-02','2026-10-31','SAR','Tenant A customer','300000000000001',
100.0000,'source movement smoke','DRAFT','aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa');
INSERT INTO sales_invoice_draft_lines
(id,tenant_id,company_id,sales_invoice_draft_id,line_number,description,quantity,unit_price,line_amount,
 inventory_item_id,inventory_warehouse_id,serial_numbers,inventory_cost_amount)
VALUES
('99999999-9999-4999-8999-999999999913','11111111-1111-4111-8111-111111111111','11111111-1111-4111-8111-111111111101',
 '99999999-9999-4999-8999-999999999912',1,'Ordinary stock sale',2.0000,30.0000,60.0000,
 '99999999-9999-4999-8999-999999999903','99999999-9999-4999-8999-999999999902','[]'::jsonb,20.0000),
('99999999-9999-4999-8999-999999999914','11111111-1111-4111-8111-111111111111','11111111-1111-4111-8111-111111111101',
 '99999999-9999-4999-8999-999999999912',2,'Serial stock sale',1.0000,40.0000,40.0000,
 '99999999-9999-4999-8999-999999999904','99999999-9999-4999-8999-999999999902','["DOC-LINK-SN-001"]'::jsonb,20.0000);

INSERT INTO inventory_movements
(id,tenant_id,company_id,movement_number,item_id,warehouse_id,movement_type,movement_date,quantity_delta,unit_cost,
 inventory_value_delta,source_type,transfer_id,purchase_invoice_draft_line_id,sales_invoice_draft_line_id,notes,created_by)
VALUES
('99999999-9999-4999-8999-999999999915','11111111-1111-4111-8111-111111111111','11111111-1111-4111-8111-111111111101',
 'DOC-SAL-ISSUE-001','99999999-9999-4999-8999-999999999903','99999999-9999-4999-8999-999999999902','ISSUE','2026-10-02',-2.0000,10.00000000,-20.0000,
 'SALES',NULL,NULL,'99999999-9999-4999-8999-999999999913','linked sales issue','aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'),
('99999999-9999-4999-8999-999999999916','11111111-1111-4111-8111-111111111111','11111111-1111-4111-8111-111111111101',
 'DOC-SAL-ISSUE-002','99999999-9999-4999-8999-999999999904','99999999-9999-4999-8999-999999999902','ISSUE','2026-10-02',-1.0000,20.00000000,-20.0000,
 'SALES',NULL,NULL,'99999999-9999-4999-8999-999999999914','linked serial sales issue','aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa');
UPDATE inventory_serial_numbers
   SET status='ISSUED',current_warehouse_id=NULL,last_movement_id='99999999-9999-4999-8999-999999999916'
 WHERE id='99999999-9999-4999-8999-999999999910';
INSERT INTO inventory_serial_events
(id,tenant_id,company_id,serial_id,item_id,movement_id,transfer_id,event_type,warehouse_id,movement_date,created_by)
VALUES ('99999999-9999-4999-8999-999999999917','11111111-1111-4111-8111-111111111111','11111111-1111-4111-8111-111111111101',
'99999999-9999-4999-8999-999999999910','99999999-9999-4999-8999-999999999904','99999999-9999-4999-8999-999999999916',NULL,
'ISSUE','99999999-9999-4999-8999-999999999902','2026-10-02','aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa');

DO $$
DECLARE
  duplicate_inserted boolean := false;
BEGIN
  IF (SELECT count(*) FROM inventory_movements WHERE source_type='PURCHASE' AND movement_number LIKE 'DOC-PUR-RCPT-%') <> 2
     OR (SELECT count(*) FROM inventory_movements WHERE source_type='SALES' AND movement_number LIKE 'DOC-SAL-ISSUE-%') <> 2 THEN
    RAISE EXCEPTION 'Expected two invoice-linked receipts and two invoice-linked issues';
  END IF;
  IF (SELECT sum(quantity_delta) FROM inventory_movements WHERE item_id='99999999-9999-4999-8999-999999999903') <> 3.0000
     OR (SELECT sum(inventory_value_delta) FROM inventory_movements WHERE item_id='99999999-9999-4999-8999-999999999903') <> 30.0000 THEN
    RAISE EXCEPTION 'Ordinary item stock quantity/value did not reconcile';
  END IF;
  IF (SELECT sum(quantity_delta) FROM inventory_movements WHERE item_id='99999999-9999-4999-8999-999999999904') <> 0.0000
     OR (SELECT sum(inventory_value_delta) FROM inventory_movements WHERE item_id='99999999-9999-4999-8999-999999999904') <> 0.0000 THEN
    RAISE EXCEPTION 'Serialized item stock quantity/value did not reconcile';
  END IF;
  IF (SELECT status FROM inventory_serial_numbers WHERE serial_number='DOC-LINK-SN-001') <> 'ISSUED'
     OR (SELECT current_warehouse_id FROM inventory_serial_numbers WHERE serial_number='DOC-LINK-SN-001') IS NOT NULL THEN
    RAISE EXCEPTION 'Serialized unit was not issued from linked sales line';
  END IF;

  BEGIN
    INSERT INTO inventory_movements
    (id,tenant_id,company_id,movement_number,item_id,warehouse_id,movement_type,movement_date,quantity_delta,unit_cost,
     inventory_value_delta,source_type,transfer_id,purchase_invoice_draft_line_id,sales_invoice_draft_line_id,notes,created_by)
    VALUES ('99999999-9999-4999-8999-999999999918','11111111-1111-4111-8111-111111111111',
     '11111111-1111-4111-8111-111111111101','DOC-PUR-DUPLICATE','99999999-9999-4999-8999-999999999903',
     '99999999-9999-4999-8999-999999999902','RECEIPT','2026-10-01',1.0000,10.00000000,10.0000,'PURCHASE',NULL,
     '99999999-9999-4999-8999-999999999906',NULL,'must not duplicate source line','aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa');
    duplicate_inserted := true;
  EXCEPTION WHEN unique_violation THEN NULL;
  END;
  IF duplicate_inserted THEN
    RAISE EXCEPTION 'Multiple stock movements must not reference the same purchase invoice line';
  END IF;

  BEGIN
    UPDATE purchase_invoice_draft_lines SET inventory_cost_amount=49.0000
     WHERE id='99999999-9999-4999-8999-999999999906';
    RAISE EXCEPTION 'Posted purchase source line must not be mutable';
  EXCEPTION WHEN check_violation OR insufficient_privilege THEN NULL;
  END;
  BEGIN
    UPDATE sales_invoice_draft_lines SET serial_numbers='[]'::jsonb
     WHERE id='99999999-9999-4999-8999-999999999914';
    RAISE EXCEPTION 'Posted sales source line must not be mutable';
  EXCEPTION WHEN check_violation OR insufficient_privilege THEN NULL;
  END;
END $;

SELECT set_config('app.tenant_id','22222222-2222-4222-8222-222222222222',true);
DO $$
BEGIN
  IF EXISTS (SELECT 1 FROM suppliers WHERE supplier_code='DOC-LINK-SUPPLIER')
     OR EXISTS (SELECT 1 FROM purchase_invoice_draft_lines WHERE id='99999999-9999-4999-8999-999999999906')
     OR EXISTS (SELECT 1 FROM sales_invoice_draft_lines WHERE id='99999999-9999-4999-8999-999999999913')
     OR EXISTS (SELECT 1 FROM inventory_movements WHERE movement_number LIKE 'DOC-%') THEN
    RAISE EXCEPTION 'tenant B can view tenant A document-linked stock records';
  END IF;
END $$;
SELECT set_config('app.tenant_id','11111111-1111-4111-8111-111111111111',true);
COMMIT;
