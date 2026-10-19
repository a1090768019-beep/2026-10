-- Atomic, tenant-scoped warehouse transfers linked to two stock-ledger legs.
BEGIN;

CREATE TABLE inventory_transfers (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL,
    company_id uuid NOT NULL,
    transfer_number varchar(72) NOT NULL
        CHECK (length(btrim(transfer_number)) > 0 AND transfer_number = upper(transfer_number)),
    item_id uuid NOT NULL,
    source_warehouse_id uuid NOT NULL,
    destination_warehouse_id uuid NOT NULL,
    transfer_date date NOT NULL,
    quantity numeric(20,4) NOT NULL CHECK (quantity > 0),
    unit_cost numeric(20,8) NOT NULL CHECK (unit_cost >= 0),
    inventory_value numeric(20,4) NOT NULL CHECK (inventory_value >= 0),
    notes varchar(1000) NOT NULL DEFAULT '',
    created_by uuid NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
    created_at timestamptz NOT NULL DEFAULT now(),
    CHECK (source_warehouse_id <> destination_warehouse_id),
    UNIQUE (tenant_id, company_id, id),
    UNIQUE (tenant_id, company_id, transfer_number),
    FOREIGN KEY (tenant_id, company_id) REFERENCES companies(tenant_id, id) ON DELETE RESTRICT,
    FOREIGN KEY (tenant_id, company_id, item_id) REFERENCES items(tenant_id, company_id, id) ON DELETE RESTRICT,
    FOREIGN KEY (tenant_id, company_id, source_warehouse_id) REFERENCES warehouses(tenant_id, company_id, id) ON DELETE RESTRICT,
    FOREIGN KEY (tenant_id, company_id, destination_warehouse_id) REFERENCES warehouses(tenant_id, company_id, id) ON DELETE RESTRICT
);
CREATE INDEX ix_inventory_transfers_company_date
    ON inventory_transfers(tenant_id, company_id, transfer_date DESC, transfer_number);

ALTER TABLE inventory_movements ADD COLUMN transfer_id uuid NULL;
ALTER TABLE inventory_movements DROP CONSTRAINT inventory_movements_source_type_check;
ALTER TABLE inventory_movements
    ADD CONSTRAINT inventory_movements_source_link_check
    CHECK (
        (source_type = 'MANUAL' AND transfer_id IS NULL)
        OR (source_type = 'TRANSFER' AND transfer_id IS NOT NULL)
    );
ALTER TABLE inventory_movements
    ADD CONSTRAINT fk_inventory_movements_transfer
    FOREIGN KEY (tenant_id, company_id, transfer_id)
    REFERENCES inventory_transfers(tenant_id, company_id, id)
    ON DELETE RESTRICT;

CREATE UNIQUE INDEX ux_inventory_movements_transfer_direction
    ON inventory_movements(tenant_id, company_id, transfer_id, movement_type)
    WHERE transfer_id IS NOT NULL;
CREATE INDEX ix_inventory_movements_transfer
    ON inventory_movements(tenant_id, company_id, transfer_id)
    WHERE transfer_id IS NOT NULL;

CREATE OR REPLACE FUNCTION prevent_inventory_transfer_mutation() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'Inventory transfers are immutable; use compensating stock movements'
        USING ERRCODE = '23514';
END $$;

CREATE TRIGGER trg_inventory_transfers_immutable
    BEFORE UPDATE OR DELETE ON inventory_transfers
    FOR EACH ROW EXECUTE FUNCTION prevent_inventory_transfer_mutation();

-- Every transfer header must commit with precisely matching issue and receipt legs.
CREATE OR REPLACE FUNCTION assert_inventory_transfer_complete() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    transfer_key uuid;
    transfer_row inventory_transfers%ROWTYPE;
    movement_count integer;
    issue_count integer;
    receipt_count integer;
    issue_quantity numeric(20,4);
    receipt_quantity numeric(20,4);
    issue_value numeric(20,4);
    receipt_value numeric(20,4);
    issue_cost numeric(20,8);
    receipt_cost numeric(20,8);
    issue_item uuid;
    receipt_item uuid;
    issue_warehouse uuid;
    receipt_warehouse uuid;
    issue_date date;
    receipt_date date;
BEGIN
    IF TG_TABLE_NAME = 'inventory_transfers' THEN
        transfer_key := NEW.id;
    ELSE
        transfer_key := NEW.transfer_id;
    END IF;

    IF transfer_key IS NULL THEN
        RETURN NULL;
    END IF;

    SELECT * INTO transfer_row FROM inventory_transfers WHERE id = transfer_key;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Inventory transfer header missing for transfer %', transfer_key USING ERRCODE = '23514';
    END IF;

    SELECT count(*)::integer,
           count(*) FILTER (WHERE movement_type = 'ISSUE')::integer,
           count(*) FILTER (WHERE movement_type = 'RECEIPT')::integer,
           max(-quantity_delta) FILTER (WHERE movement_type = 'ISSUE'),
           max(quantity_delta) FILTER (WHERE movement_type = 'RECEIPT'),
           max(-inventory_value_delta) FILTER (WHERE movement_type = 'ISSUE'),
           max(inventory_value_delta) FILTER (WHERE movement_type = 'RECEIPT'),
           max(unit_cost) FILTER (WHERE movement_type = 'ISSUE'),
           max(unit_cost) FILTER (WHERE movement_type = 'RECEIPT'),
           max(item_id) FILTER (WHERE movement_type = 'ISSUE'),
           max(item_id) FILTER (WHERE movement_type = 'RECEIPT'),
           max(warehouse_id) FILTER (WHERE movement_type = 'ISSUE'),
           max(warehouse_id) FILTER (WHERE movement_type = 'RECEIPT'),
           max(movement_date) FILTER (WHERE movement_type = 'ISSUE'),
           max(movement_date) FILTER (WHERE movement_type = 'RECEIPT')
      INTO movement_count, issue_count, receipt_count,
           issue_quantity, receipt_quantity, issue_value, receipt_value,
           issue_cost, receipt_cost, issue_item, receipt_item,
           issue_warehouse, receipt_warehouse, issue_date, receipt_date
      FROM inventory_movements
     WHERE transfer_id = transfer_key AND tenant_id = transfer_row.tenant_id
       AND company_id = transfer_row.company_id AND source_type = 'TRANSFER';

    IF movement_count <> 2 OR issue_count <> 1 OR receipt_count <> 1
       OR issue_quantity IS DISTINCT FROM transfer_row.quantity
       OR receipt_quantity IS DISTINCT FROM transfer_row.quantity
       OR issue_value IS DISTINCT FROM transfer_row.inventory_value
       OR receipt_value IS DISTINCT FROM transfer_row.inventory_value
       OR issue_cost IS DISTINCT FROM transfer_row.unit_cost
       OR receipt_cost IS DISTINCT FROM transfer_row.unit_cost
       OR issue_item IS DISTINCT FROM transfer_row.item_id
       OR receipt_item IS DISTINCT FROM transfer_row.item_id
       OR issue_warehouse IS DISTINCT FROM transfer_row.source_warehouse_id
       OR receipt_warehouse IS DISTINCT FROM transfer_row.destination_warehouse_id
       OR issue_date IS DISTINCT FROM transfer_row.transfer_date
       OR receipt_date IS DISTINCT FROM transfer_row.transfer_date THEN
        RAISE EXCEPTION 'Inventory transfer % must commit with matching issue and receipt movements', transfer_key
            USING ERRCODE = '23514';
    END IF;

    RETURN NULL;
END $$;

CREATE CONSTRAINT TRIGGER trg_inventory_transfer_complete_header
    AFTER INSERT ON inventory_transfers
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION assert_inventory_transfer_complete();

CREATE CONSTRAINT TRIGGER trg_inventory_transfer_complete_movement
    AFTER INSERT ON inventory_movements
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW WHEN (NEW.transfer_id IS NOT NULL)
    EXECUTE FUNCTION assert_inventory_transfer_complete();

ALTER TABLE inventory_transfers ENABLE ROW LEVEL SECURITY;
ALTER TABLE inventory_transfers FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON inventory_transfers
    USING (tenant_id = app_current_tenant_id())
    WITH CHECK (tenant_id = app_current_tenant_id());

COMMENT ON TABLE inventory_transfers IS
    'Immutable warehouse transfer header. Each committed transfer must have a matching issue and receipt in the same transaction.';
COMMENT ON COLUMN inventory_movements.transfer_id IS
    'Non-null for the paired immutable issue/receipt legs of a warehouse transfer.';

COMMIT;
