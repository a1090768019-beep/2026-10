-- Tenant-scoped item catalog, warehouses and immutable quantity/value movements.
-- Inventory costing uses a moving weighted-average basis per item and warehouse.
BEGIN;

CREATE TABLE items (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL,
    company_id uuid NOT NULL,
    item_code varchar(64) NOT NULL
        CHECK (length(btrim(item_code)) > 0 AND item_code = upper(item_code)),
    barcode varchar(100) NULL CHECK (barcode IS NULL OR (length(btrim(barcode)) > 0 AND barcode = btrim(barcode))),
    name_ar varchar(200) NOT NULL CHECK (length(btrim(name_ar)) > 0),
    name_en varchar(200) NOT NULL CHECK (length(btrim(name_en)) > 0),
    unit_code varchar(16) NOT NULL DEFAULT 'EA'
        CHECK (unit_code = upper(unit_code) AND length(btrim(unit_code)) BETWEEN 1 AND 16),
    is_active boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, company_id, id),
    UNIQUE (tenant_id, company_id, item_code),
    FOREIGN KEY (tenant_id, company_id) REFERENCES companies(tenant_id, id) ON DELETE RESTRICT
);
CREATE UNIQUE INDEX ux_items_tenant_company_barcode
    ON items(tenant_id, company_id, barcode) WHERE barcode IS NOT NULL;
CREATE INDEX ix_items_tenant_company_name
    ON items(tenant_id, company_id, name_ar, item_code);

CREATE TABLE warehouses (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL,
    company_id uuid NOT NULL,
    warehouse_code varchar(32) NOT NULL
        CHECK (length(btrim(warehouse_code)) > 0 AND warehouse_code = upper(warehouse_code)),
    name varchar(200) NOT NULL CHECK (length(btrim(name)) > 0),
    is_active boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, company_id, id),
    UNIQUE (tenant_id, company_id, warehouse_code),
    FOREIGN KEY (tenant_id, company_id) REFERENCES companies(tenant_id, id) ON DELETE RESTRICT
);
CREATE INDEX ix_warehouses_tenant_company_name
    ON warehouses(tenant_id, company_id, name, warehouse_code);

CREATE TABLE inventory_movements (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL,
    company_id uuid NOT NULL,
    movement_number varchar(80) NOT NULL
        CHECK (length(btrim(movement_number)) > 0 AND movement_number = upper(movement_number)),
    item_id uuid NOT NULL,
    warehouse_id uuid NOT NULL,
    movement_type varchar(16) NOT NULL CHECK (movement_type IN ('RECEIPT', 'ISSUE')),
    movement_date date NOT NULL,
    quantity_delta numeric(20,4) NOT NULL CHECK (quantity_delta <> 0),
    unit_cost numeric(20,8) NOT NULL CHECK (unit_cost >= 0),
    inventory_value_delta numeric(20,4) NOT NULL,
    source_type varchar(32) NOT NULL DEFAULT 'MANUAL' CHECK (source_type IN ('MANUAL')),
    notes varchar(1000) NOT NULL DEFAULT '',
    created_by uuid NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
    created_at timestamptz NOT NULL DEFAULT now(),
    CHECK (
        (movement_type = 'RECEIPT' AND quantity_delta > 0 AND inventory_value_delta >= 0)
        OR
        (movement_type = 'ISSUE' AND quantity_delta < 0 AND inventory_value_delta <= 0)
    ),
    UNIQUE (tenant_id, company_id, id),
    UNIQUE (tenant_id, company_id, movement_number),
    FOREIGN KEY (tenant_id, company_id) REFERENCES companies(tenant_id, id) ON DELETE RESTRICT,
    FOREIGN KEY (tenant_id, company_id, item_id) REFERENCES items(tenant_id, company_id, id) ON DELETE RESTRICT,
    FOREIGN KEY (tenant_id, company_id, warehouse_id) REFERENCES warehouses(tenant_id, company_id, id) ON DELETE RESTRICT
);
CREATE INDEX ix_inventory_movements_stock_card
    ON inventory_movements(tenant_id, company_id, warehouse_id, item_id, movement_date, created_at);
CREATE INDEX ix_inventory_movements_item_date
    ON inventory_movements(tenant_id, company_id, item_id, movement_date DESC);

CREATE OR REPLACE FUNCTION prevent_inventory_movement_mutation() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'Inventory movements are immutable; use a compensating receipt or issue'
        USING ERRCODE = '23514';
END $$;

CREATE TRIGGER trg_inventory_movements_immutable
    BEFORE UPDATE OR DELETE ON inventory_movements
    FOR EACH ROW EXECUTE FUNCTION prevent_inventory_movement_mutation();

-- Database-level guard prevents negative stock even if a caller bypasses the API's pre-check.
-- The lock-key format matches InventoryEndpoints.RecordMovement.
CREATE OR REPLACE FUNCTION guard_inventory_movement_balance() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    current_quantity numeric(20,4);
    current_value numeric(20,4);
    balance_lock text;
BEGIN
    balance_lock := 'inventory-balance:' || NEW.tenant_id::text || ':' || NEW.company_id::text
                    || ':' || NEW.warehouse_id::text || ':' || NEW.item_id::text;
    PERFORM pg_advisory_xact_lock(hashtextextended(balance_lock, 0));

    SELECT COALESCE(sum(quantity_delta), 0), COALESCE(sum(inventory_value_delta), 0)
      INTO current_quantity, current_value
      FROM inventory_movements
     WHERE tenant_id = NEW.tenant_id
       AND company_id = NEW.company_id
       AND item_id = NEW.item_id
       AND warehouse_id = NEW.warehouse_id;

    IF NEW.movement_type = 'ISSUE' THEN
        IF -NEW.quantity_delta > current_quantity THEN
            RAISE EXCEPTION 'Inventory issue would make on-hand quantity negative'
                USING ERRCODE = '23514';
        END IF;
        IF -NEW.inventory_value_delta > current_value THEN
            RAISE EXCEPTION 'Inventory issue would make inventory value negative'
                USING ERRCODE = '23514';
        END IF;
    ELSIF NEW.movement_type = 'RECEIPT' THEN
        IF current_quantity + NEW.quantity_delta > 9999999999999999.9999
           OR current_value + NEW.inventory_value_delta > 9999999999999999.9999 THEN
            RAISE EXCEPTION 'Inventory receipt exceeds supported balance precision'
                USING ERRCODE = '23514';
        END IF;
    END IF;

    RETURN NEW;
END $$;

CREATE TRIGGER trg_inventory_movements_balance_guard
    BEFORE INSERT ON inventory_movements
    FOR EACH ROW EXECUTE FUNCTION guard_inventory_movement_balance();

ALTER TABLE items ENABLE ROW LEVEL SECURITY;
ALTER TABLE items FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON items
    USING (tenant_id = app_current_tenant_id())
    WITH CHECK (tenant_id = app_current_tenant_id());

ALTER TABLE warehouses ENABLE ROW LEVEL SECURITY;
ALTER TABLE warehouses FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON warehouses
    USING (tenant_id = app_current_tenant_id())
    WITH CHECK (tenant_id = app_current_tenant_id());

ALTER TABLE inventory_movements ENABLE ROW LEVEL SECURITY;
ALTER TABLE inventory_movements FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON inventory_movements
    USING (tenant_id = app_current_tenant_id())
    WITH CHECK (tenant_id = app_current_tenant_id());

COMMENT ON TABLE inventory_movements IS
    'Immutable stock quantity/value ledger. Moving weighted-average cost is computed per item and warehouse. Transfers, serial-level tracking and purchase/sales source integration are not implemented by this migration.';
COMMENT ON COLUMN inventory_movements.inventory_value_delta IS
    'Signed inventory asset value change. Receipts increase quantity/value; issues decrease quantity/value at current moving weighted-average cost.';
COMMIT;
