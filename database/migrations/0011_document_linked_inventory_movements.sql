-- Optional item/warehouse references on purchase/sales invoice lines, plus their immutable stock movements.
BEGIN;

ALTER TABLE purchase_invoice_draft_lines
    ADD COLUMN inventory_item_id uuid NULL,
    ADD COLUMN inventory_warehouse_id uuid NULL,
    ADD COLUMN serial_numbers jsonb NOT NULL DEFAULT '[]'::jsonb,
    ADD COLUMN inventory_cost_amount numeric(20,4) NULL,
    ADD CONSTRAINT ck_purchase_draft_line_inventory_pair
        CHECK ((inventory_item_id IS NULL) = (inventory_warehouse_id IS NULL)),
    ADD CONSTRAINT ck_purchase_draft_line_serial_array
        CHECK (jsonb_typeof(serial_numbers) = 'array'),
    ADD CONSTRAINT ck_purchase_draft_line_inventory_cost
        CHECK (inventory_cost_amount IS NULL OR inventory_cost_amount >= 0),
    ADD CONSTRAINT ux_purchase_draft_line_tenant_company_id UNIQUE (tenant_id, company_id, id),
    ADD CONSTRAINT fk_purchase_draft_line_inventory_item
        FOREIGN KEY (tenant_id, company_id, inventory_item_id)
        REFERENCES items(tenant_id, company_id, id) ON DELETE RESTRICT,
    ADD CONSTRAINT fk_purchase_draft_line_inventory_warehouse
        FOREIGN KEY (tenant_id, company_id, inventory_warehouse_id)
        REFERENCES warehouses(tenant_id, company_id, id) ON DELETE RESTRICT,
    ADD CONSTRAINT ck_purchase_draft_line_inventory_cost_link
        CHECK (inventory_item_id IS NOT NULL OR inventory_cost_amount IS NULL);

ALTER TABLE sales_invoice_draft_lines
    ADD COLUMN inventory_item_id uuid NULL,
    ADD COLUMN inventory_warehouse_id uuid NULL,
    ADD COLUMN serial_numbers jsonb NOT NULL DEFAULT '[]'::jsonb,
    ADD COLUMN inventory_cost_amount numeric(20,4) NULL,
    ADD CONSTRAINT ck_sales_draft_line_inventory_pair
        CHECK ((inventory_item_id IS NULL) = (inventory_warehouse_id IS NULL)),
    ADD CONSTRAINT ck_sales_draft_line_serial_array
        CHECK (jsonb_typeof(serial_numbers) = 'array'),
    ADD CONSTRAINT ck_sales_draft_line_inventory_cost
        CHECK (inventory_cost_amount IS NULL OR inventory_cost_amount >= 0),
    ADD CONSTRAINT ux_sales_draft_line_tenant_company_id UNIQUE (tenant_id, company_id, id),
    ADD CONSTRAINT fk_sales_draft_line_inventory_item
        FOREIGN KEY (tenant_id, company_id, inventory_item_id)
        REFERENCES items(tenant_id, company_id, id) ON DELETE RESTRICT,
    ADD CONSTRAINT fk_sales_draft_line_inventory_warehouse
        FOREIGN KEY (tenant_id, company_id, inventory_warehouse_id)
        REFERENCES warehouses(tenant_id, company_id, id) ON DELETE RESTRICT,
    ADD CONSTRAINT ck_sales_draft_line_inventory_cost_link
        CHECK (inventory_item_id IS NOT NULL OR inventory_cost_amount IS NULL);

CREATE INDEX ix_purchase_draft_lines_inventory_item
    ON purchase_invoice_draft_lines(tenant_id, company_id, inventory_item_id, inventory_warehouse_id)
    WHERE inventory_item_id IS NOT NULL;
CREATE INDEX ix_sales_draft_lines_inventory_item
    ON sales_invoice_draft_lines(tenant_id, company_id, inventory_item_id, inventory_warehouse_id)
    WHERE inventory_item_id IS NOT NULL;

ALTER TABLE inventory_movements
    ADD COLUMN purchase_invoice_draft_line_id uuid NULL,
    ADD COLUMN sales_invoice_draft_line_id uuid NULL;

ALTER TABLE inventory_movements DROP CONSTRAINT inventory_movements_source_link_check;
ALTER TABLE inventory_movements
    ADD CONSTRAINT inventory_movements_source_link_check CHECK (
        (source_type = 'MANUAL' AND transfer_id IS NULL
            AND purchase_invoice_draft_line_id IS NULL AND sales_invoice_draft_line_id IS NULL)
        OR (source_type = 'TRANSFER' AND transfer_id IS NOT NULL
            AND purchase_invoice_draft_line_id IS NULL AND sales_invoice_draft_line_id IS NULL)
        OR (source_type = 'PURCHASE' AND transfer_id IS NULL
            AND purchase_invoice_draft_line_id IS NOT NULL AND sales_invoice_draft_line_id IS NULL)
        OR (source_type = 'SALES' AND transfer_id IS NULL
            AND purchase_invoice_draft_line_id IS NULL AND sales_invoice_draft_line_id IS NOT NULL)
    );

ALTER TABLE inventory_movements
    ADD CONSTRAINT fk_inventory_movement_purchase_line
        FOREIGN KEY (tenant_id, company_id, purchase_invoice_draft_line_id)
        REFERENCES purchase_invoice_draft_lines(tenant_id, company_id, id) ON DELETE RESTRICT,
    ADD CONSTRAINT fk_inventory_movement_sales_line
        FOREIGN KEY (tenant_id, company_id, sales_invoice_draft_line_id)
        REFERENCES sales_invoice_draft_lines(tenant_id, company_id, id) ON DELETE RESTRICT;

CREATE UNIQUE INDEX ux_inventory_movement_purchase_source_line
    ON inventory_movements(tenant_id, company_id, purchase_invoice_draft_line_id)
    WHERE purchase_invoice_draft_line_id IS NOT NULL;
CREATE UNIQUE INDEX ux_inventory_movement_sales_source_line
    ON inventory_movements(tenant_id, company_id, sales_invoice_draft_line_id)
    WHERE sales_invoice_draft_line_id IS NOT NULL;

CREATE OR REPLACE FUNCTION assert_inventory_document_movement_source() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    line_item_id uuid;
    line_warehouse_id uuid;
    line_quantity numeric(20,4);
    line_cost numeric(20,4);
    document_date date;
BEGIN
    IF NEW.source_type = 'PURCHASE' THEN
        SELECT line.inventory_item_id, line.inventory_warehouse_id, line.quantity,
               line.inventory_cost_amount, draft.issue_date
          INTO line_item_id, line_warehouse_id, line_quantity, line_cost, document_date
          FROM purchase_invoice_draft_lines line
          JOIN purchase_invoice_drafts draft
            ON draft.tenant_id = line.tenant_id AND draft.company_id = line.company_id
           AND draft.id = line.purchase_invoice_draft_id
         WHERE line.tenant_id = NEW.tenant_id AND line.company_id = NEW.company_id
           AND line.id = NEW.purchase_invoice_draft_line_id;
        IF NOT FOUND OR line_item_id IS NULL OR line_warehouse_id IS NULL OR line_cost IS NULL
           OR NEW.movement_type <> 'RECEIPT'
           OR NEW.item_id IS DISTINCT FROM line_item_id
           OR NEW.warehouse_id IS DISTINCT FROM line_warehouse_id
           OR NEW.movement_date IS DISTINCT FROM document_date
           OR NEW.quantity_delta IS DISTINCT FROM line_quantity
           OR NEW.inventory_value_delta IS DISTINCT FROM line_cost
           OR NEW.sales_invoice_draft_line_id IS NOT NULL THEN
            RAISE EXCEPTION 'Purchase stock movement must match its linked inventory line'
                USING ERRCODE = '23514';
        END IF;
    ELSIF NEW.source_type = 'SALES' THEN
        SELECT line.inventory_item_id, line.inventory_warehouse_id, line.quantity,
               line.inventory_cost_amount, draft.issue_date
          INTO line_item_id, line_warehouse_id, line_quantity, line_cost, document_date
          FROM sales_invoice_draft_lines line
          JOIN sales_invoice_drafts draft
            ON draft.tenant_id = line.tenant_id AND draft.company_id = line.company_id
           AND draft.id = line.sales_invoice_draft_id
         WHERE line.tenant_id = NEW.tenant_id AND line.company_id = NEW.company_id
           AND line.id = NEW.sales_invoice_draft_line_id;
        IF NOT FOUND OR line_item_id IS NULL OR line_warehouse_id IS NULL OR line_cost IS NULL
           OR NEW.movement_type <> 'ISSUE'
           OR NEW.item_id IS DISTINCT FROM line_item_id
           OR NEW.warehouse_id IS DISTINCT FROM line_warehouse_id
           OR NEW.movement_date IS DISTINCT FROM document_date
           OR NEW.quantity_delta IS DISTINCT FROM -line_quantity
           OR NEW.inventory_value_delta IS DISTINCT FROM -line_cost
           OR NEW.purchase_invoice_draft_line_id IS NOT NULL THEN
            RAISE EXCEPTION 'Sales stock movement must match its linked inventory line'
                USING ERRCODE = '23514';
        END IF;
    END IF;
    RETURN NULL;
END $$;

CREATE CONSTRAINT TRIGGER trg_inventory_document_movement_source_valid
    AFTER INSERT ON inventory_movements
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION assert_inventory_document_movement_source();

-- Serial-completeness rules must recognise invoice-sourced receipt/issue movements as well as manual movements.
CREATE OR REPLACE FUNCTION assert_inventory_serial_movement() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    movement_key uuid;
    movement_row inventory_movements%ROWTYPE;
    tracked boolean;
    event_count integer;
    invalid_event_count integer;
    expected_event_type varchar(16);
    expected_count integer;
BEGIN
    IF TG_TABLE_NAME = 'inventory_movements' THEN
        movement_key := NEW.id;
    ELSE
        movement_key := NEW.movement_id;
    END IF;

    SELECT * INTO movement_row FROM inventory_movements WHERE id = movement_key;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Inventory movement missing for serial validation' USING ERRCODE = '23514';
    END IF;

    SELECT tracks_serials INTO tracked FROM items
     WHERE tenant_id = movement_row.tenant_id
       AND company_id = movement_row.company_id
       AND id = movement_row.item_id;

    IF movement_row.source_type IN ('MANUAL', 'PURCHASE') AND movement_row.movement_type = 'RECEIPT' THEN
        expected_event_type := 'RECEIPT';
    ELSIF movement_row.source_type IN ('MANUAL', 'SALES') AND movement_row.movement_type = 'ISSUE' THEN
        expected_event_type := 'ISSUE';
    ELSIF movement_row.source_type = 'TRANSFER' AND movement_row.movement_type = 'ISSUE' THEN
        expected_event_type := 'TRANSFER_OUT';
    ELSIF movement_row.source_type = 'TRANSFER' AND movement_row.movement_type = 'RECEIPT' THEN
        expected_event_type := 'TRANSFER_IN';
    ELSE
        expected_event_type := NULL;
    END IF;

    SELECT count(*)::integer,
           count(*) FILTER (
               WHERE item_id IS DISTINCT FROM movement_row.item_id
                  OR warehouse_id IS DISTINCT FROM movement_row.warehouse_id
                  OR movement_date IS DISTINCT FROM movement_row.movement_date
                  OR transfer_id IS DISTINCT FROM movement_row.transfer_id
                  OR event_type IS DISTINCT FROM expected_event_type
           )::integer
      INTO event_count, invalid_event_count
      FROM inventory_serial_events
     WHERE tenant_id = movement_row.tenant_id
       AND company_id = movement_row.company_id
       AND movement_id = movement_row.id;

    IF NOT COALESCE(tracked, false) THEN
        IF event_count <> 0 THEN
            RAISE EXCEPTION 'Serial events are not allowed for non-serialized items' USING ERRCODE = '23514';
        END IF;
        RETURN NULL;
    END IF;

    IF expected_event_type IS NULL
       OR movement_row.quantity_delta <> trunc(movement_row.quantity_delta)
       OR abs(movement_row.quantity_delta) > 1000 THEN
        RAISE EXCEPTION 'Serial-tracked movement requires supported type and whole quantity up to 1000' USING ERRCODE = '23514';
    END IF;

    expected_count := abs(movement_row.quantity_delta)::integer;
    IF event_count <> expected_count OR invalid_event_count <> 0 THEN
        RAISE EXCEPTION 'Serial-tracked movement % needs % matching serial events, found %',
            movement_row.movement_number, expected_count, event_count USING ERRCODE = '23514';
    END IF;
    RETURN NULL;
END $$;

COMMIT;
