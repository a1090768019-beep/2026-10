-- Exercise current serial state across receipt, warehouse transfer and issue.
BEGIN;
SELECT set_config('app.tenant_id', '11111111-1111-4111-8111-111111111111', true);

INSERT INTO warehouses (id,tenant_id,company_id,warehouse_code,name) VALUES
 ('dddddddd-dddd-4ddd-8ddd-ddddddddddd1','11111111-1111-4111-8111-111111111111','11111111-1111-4111-8111-111111111101','CI-SERIAL-A','CI Serial Source'),
 ('dddddddd-dddd-4ddd-8ddd-ddddddddddd2','11111111-1111-4111-8111-111111111111','11111111-1111-4111-8111-111111111101','CI-SERIAL-B','CI Serial Destination');

INSERT INTO items (id,tenant_id,company_id,item_code,barcode,name_ar,name_en,unit_code,tracks_serials)
VALUES ('dddddddd-dddd-4ddd-8ddd-ddddddddddd3','11111111-1111-4111-8111-111111111111','11111111-1111-4111-8111-111111111101',
        'CI-SERIAL-TRACKED','CI-SERIAL-BARCODE','صنف متسلسل للاختبار','CI serial item','EA',true);

INSERT INTO inventory_movements (id,tenant_id,company_id,movement_number,item_id,warehouse_id,movement_type,movement_date,
 quantity_delta,unit_cost,inventory_value_delta,source_type,notes,created_by)
VALUES ('dddddddd-dddd-4ddd-8ddd-ddddddddddd4','11111111-1111-4111-8111-111111111111','11111111-1111-4111-8111-111111111101',
 'CI-SERIAL-RECEIPT','dddddddd-dddd-4ddd-8ddd-ddddddddddd3','dddddddd-dddd-4ddd-8ddd-ddddddddddd1','RECEIPT','2026-10-01',
 2.0000,10.00000000,20.0000,'MANUAL','serial receipt','aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa');

INSERT INTO inventory_serial_numbers (id,tenant_id,company_id,item_id,serial_number,status,current_warehouse_id,first_received_movement_id,last_movement_id) VALUES
 ('dddddddd-dddd-4ddd-8ddd-ddddddddddd5','11111111-1111-4111-8111-111111111111','11111111-1111-4111-8111-111111111101','dddddddd-dddd-4ddd-8ddd-ddddddddddd3','CI-SN-001','IN_STOCK','dddddddd-dddd-4ddd-8ddd-ddddddddddd1','dddddddd-dddd-4ddd-8ddd-ddddddddddd4','dddddddd-dddd-4ddd-8ddd-ddddddddddd4'),
 ('dddddddd-dddd-4ddd-8ddd-ddddddddddd6','11111111-1111-4111-8111-111111111111','11111111-1111-4111-8111-111111111101','dddddddd-dddd-4ddd-8ddd-ddddddddddd3','CI-SN-002','IN_STOCK','dddddddd-dddd-4ddd-8ddd-ddddddddddd1','dddddddd-dddd-4ddd-8ddd-ddddddddddd4','dddddddd-dddd-4ddd-8ddd-ddddddddddd4');

INSERT INTO inventory_serial_events (id,tenant_id,company_id,serial_id,item_id,movement_id,transfer_id,event_type,warehouse_id,movement_date,created_by) VALUES
 ('dddddddd-dddd-4ddd-8ddd-ddddddddddd7','11111111-1111-4111-8111-111111111111','11111111-1111-4111-8111-111111111101','dddddddd-dddd-4ddd-8ddd-ddddddddddd5','dddddddd-dddd-4ddd-8ddd-ddddddddddd3','dddddddd-dddd-4ddd-8ddd-ddddddddddd4',NULL,'RECEIPT','dddddddd-dddd-4ddd-8ddd-ddddddddddd1','2026-10-01','aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'),
 ('dddddddd-dddd-4ddd-8ddd-ddddddddddd8','11111111-1111-4111-8111-111111111111','11111111-1111-4111-8111-111111111101','dddddddd-dddd-4ddd-8ddd-ddddddddddd6','dddddddd-dddd-4ddd-8ddd-ddddddddddd3','dddddddd-dddd-4ddd-8ddd-ddddddddddd4',NULL,'RECEIPT','dddddddd-dddd-4ddd-8ddd-ddddddddddd1','2026-10-01','aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa');

INSERT INTO inventory_transfers (id,tenant_id,company_id,transfer_number,item_id,source_warehouse_id,destination_warehouse_id,transfer_date,quantity,unit_cost,inventory_value,notes,created_by)
VALUES ('dddddddd-dddd-4ddd-8ddd-ddddddddddd9','11111111-1111-4111-8111-111111111111','11111111-1111-4111-8111-111111111101','CI-SERIAL-TRANSFER',
 'dddddddd-dddd-4ddd-8ddd-ddddddddddd3','dddddddd-dddd-4ddd-8ddd-ddddddddddd1','dddddddd-dddd-4ddd-8ddd-ddddddddddd2','2026-10-02',1.0000,10.00000000,10.0000,'serial transfer','aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa');

INSERT INTO inventory_movements (id,tenant_id,company_id,movement_number,item_id,warehouse_id,movement_type,movement_date,quantity_delta,unit_cost,inventory_value_delta,source_type,transfer_id,notes,created_by) VALUES
 ('dddddddd-dddd-4ddd-8ddd-dddddddddda1','11111111-1111-4111-8111-111111111111','11111111-1111-4111-8111-111111111101','CI-SERIAL-TRANSFER-OUT','dddddddd-dddd-4ddd-8ddd-ddddddddddd3','dddddddd-dddd-4ddd-8ddd-ddddddddddd1','ISSUE','2026-10-02',-1.0000,10.00000000,-10.0000,'TRANSFER','dddddddd-dddd-4ddd-8ddd-ddddddddddd9','serial transfer','aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'),
 ('dddddddd-dddd-4ddd-8ddd-dddddddddda2','11111111-1111-4111-8111-111111111111','11111111-1111-4111-8111-111111111101','CI-SERIAL-TRANSFER-IN','dddddddd-dddd-4ddd-8ddd-ddddddddddd3','dddddddd-dddd-4ddd-8ddd-ddddddddddd2','RECEIPT','2026-10-02',1.0000,10.00000000,10.0000,'TRANSFER','dddddddd-dddd-4ddd-8ddd-ddddddddddd9','serial transfer','aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa');

UPDATE inventory_serial_numbers SET current_warehouse_id='dddddddd-dddd-4ddd-8ddd-ddddddddddd2',last_movement_id='dddddddd-dddd-4ddd-8ddd-dddddddddda2'
 WHERE id='dddddddd-dddd-4ddd-8ddd-ddddddddddd5';
INSERT INTO inventory_serial_events (id,tenant_id,company_id,serial_id,item_id,movement_id,transfer_id,event_type,warehouse_id,movement_date,created_by) VALUES
 ('dddddddd-dddd-4ddd-8ddd-dddddddddda3','11111111-1111-4111-8111-111111111111','11111111-1111-4111-8111-111111111101','dddddddd-dddd-4ddd-8ddd-ddddddddddd5','dddddddd-dddd-4ddd-8ddd-ddddddddddd3','dddddddd-dddd-4ddd-8ddd-dddddddddda1','dddddddd-dddd-4ddd-8ddd-ddddddddddd9','TRANSFER_OUT','dddddddd-dddd-4ddd-8ddd-ddddddddddd1','2026-10-02','aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'),
 ('dddddddd-dddd-4ddd-8ddd-dddddddddda4','11111111-1111-4111-8111-111111111111','11111111-1111-4111-8111-111111111101','dddddddd-dddd-4ddd-8ddd-ddddddddddd5','dddddddd-dddd-4ddd-8ddd-ddddddddddd3','dddddddd-dddd-4ddd-8ddd-dddddddddda2','dddddddd-dddd-4ddd-8ddd-ddddddddddd9','TRANSFER_IN','dddddddd-dddd-4ddd-8ddd-ddddddddddd2','2026-10-02','aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa');

INSERT INTO inventory_movements (id,tenant_id,company_id,movement_number,item_id,warehouse_id,movement_type,movement_date,quantity_delta,unit_cost,inventory_value_delta,source_type,notes,created_by)
VALUES ('dddddddd-dddd-4ddd-8ddd-dddddddddda5','11111111-1111-4111-8111-111111111111','11111111-1111-4111-8111-111111111101','CI-SERIAL-ISSUE','dddddddd-dddd-4ddd-8ddd-ddddddddddd3','dddddddd-dddd-4ddd-8ddd-ddddddddddd1','ISSUE','2026-10-03',-1.0000,10.00000000,-10.0000,'MANUAL','serial issue','aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa');
UPDATE inventory_serial_numbers SET status='ISSUED',current_warehouse_id=NULL,last_movement_id='dddddddd-dddd-4ddd-8ddd-dddddddddda5'
 WHERE id='dddddddd-dddd-4ddd-8ddd-ddddddddddd6';
INSERT INTO inventory_serial_events (id,tenant_id,company_id,serial_id,item_id,movement_id,transfer_id,event_type,warehouse_id,movement_date,created_by)
VALUES ('dddddddd-dddd-4ddd-8ddd-dddddddddda6','11111111-1111-4111-8111-111111111111','11111111-1111-4111-8111-111111111101','dddddddd-dddd-4ddd-8ddd-ddddddddddd6','dddddddd-dddd-4ddd-8ddd-ddddddddddd3','dddddddd-dddd-4ddd-8ddd-dddddddddda5',NULL,'ISSUE','dddddddd-dddd-4ddd-8ddd-ddddddddddd1','2026-10-03','aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa');

DO $$
BEGIN
  IF (SELECT count(*) FROM inventory_serial_numbers WHERE company_id='11111111-1111-4111-8111-111111111101' AND item_id='dddddddd-dddd-4ddd-8ddd-ddddddddddd3') <> 2 THEN
    RAISE EXCEPTION 'Expected two serial identities';
  END IF;
  IF (SELECT count(*) FROM inventory_serial_events WHERE company_id='11111111-1111-4111-8111-111111111101' AND item_id='dddddddd-dddd-4ddd-8ddd-ddddddddddd3') <> 5 THEN
    RAISE EXCEPTION 'Expected five serial events';
  END IF;
  IF (SELECT current_warehouse_id FROM inventory_serial_numbers WHERE serial_number='CI-SN-001') <> 'dddddddd-dddd-4ddd-8ddd-ddddddddddd2'::uuid THEN
    RAISE EXCEPTION 'CI-SN-001 must be in destination warehouse';
  END IF;
  IF (SELECT status FROM inventory_serial_numbers WHERE serial_number='CI-SN-002') <> 'ISSUED'
     OR (SELECT current_warehouse_id FROM inventory_serial_numbers WHERE serial_number='CI-SN-002') IS NOT NULL THEN
    RAISE EXCEPTION 'CI-SN-002 must be issued without a warehouse';
  END IF;
END $$;

SELECT set_config('app.tenant_id','22222222-2222-4222-8222-222222222222',true);
DO $$
BEGIN
  IF EXISTS (SELECT 1 FROM inventory_serial_numbers WHERE serial_number LIKE 'CI-SN-%')
     OR EXISTS (SELECT 1 FROM inventory_serial_events WHERE item_id='dddddddd-dddd-4ddd-8ddd-ddddddddddd3') THEN
    RAISE EXCEPTION 'tenant B can view tenant A serial records';
  END IF;
END $$;
SELECT set_config('app.tenant_id','11111111-1111-4111-8111-111111111111',true);
COMMIT;
