-- Serial identity, location and append-only history for serialized inventory items.
BEGIN;

ALTER TABLE items ADD COLUMN tracks_serials boolean NOT NULL DEFAULT false;

CREATE TABLE inventory_serial_numbers (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL,
    company_id uuid NOT NULL,
    item_id uuid NOT NULL,
    serial_number varchar(120) NOT NULL
        CHECK (length(btrim(serial_number)) BETWEEN 1 AND 120 AND serial_number = upper(btrim(serial_number))),
    status varchar(16) NOT NULL CHECK (status IN ('IN_STOCK', 'ISSUED')),
    current_warehouse_id uuid NULL,
    first_received_movement_id uuid NOT NULL REFERENCES inventory_movements(id) ON DELETE RESTRICT,
    last_movement_id uuid NOT NULL REFERENCES inventory_movements(id) ON DELETE RESTRICT,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CHECK ((status = 'IN_STOCK' AND current_warehouse_id IS NOT NULL)
        OR (status = 'ISSUED' AND current_warehouse_id IS NULL)),
    UNIQUE (tenant_id, company_id, id),
    UNIQUE (tenant_id, company_id, serial_number),
    FOREIGN KEY (tenant_id, company_id) REFERENCES companies(tenant_id, id) ON DELETE RESTRICT,
    FOREIGN KEY (tenant_id, company_id, item_id) REFERENCES items(tenant_id, company_id, id) ON DELETE RESTRICT,
    FOREIGN KEY (tenant_id, company_id, current_warehouse_id)
        REFERENCES warehouses(tenant_id, company_id, id) ON DELETE RESTRICT
);
CREATE INDEX ix_inventory_serial_numbers_item_status
    ON inventory_serial_numbers(tenant_id, company_id, item_id, status, current_warehouse_id);

CREATE TABLE inventory_serial_events (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL,
    company_id uuid NOT NULL,
    serial_id uuid NOT NULL,
    item_id uuid NOT NULL,
    movement_id uuid NOT NULL REFERENCES inventory_movements(id) ON DELETE RESTRICT,
    transfer_id uuid NULL,
    event_type varchar(16) NOT NULL CHECK (event_type IN ('RECEIPT', 'ISSUE', 'TRANSFER_OUT', 'TRANSFER_IN')),
    warehouse_id uuid NOT NULL,
    movement_date date NOT NULL,
    created_by uuid NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
    created_at timestamptz NOT NULL DEFAULT now(),
    CHECK (
        (event_type IN ('RECEIPT', 'ISSUE') AND transfer_id IS NULL)
        OR (event_type IN ('TRANSFER_OUT', 'TRANSFER_IN') AND transfer_id IS NOT NULL)
    ),
    UNIQUE (tenant_id, company_id, id),
    UNIQUE (tenant_id, company_id, serial_id, movement_id, event_type),
    FOREIGN KEY (tenant_id, company_id) REFERENCES companies(tenant_id, id) ON DELETE RESTRICT,
    FOREIGN KEY (tenant_id, company_id, serial_id)
        REFERENCES inventory_serial_numbers(tenant_id, company_id, id) ON DELETE RESTRICT,
    FOREIGN KEY (tenant_id, company_id, item_id)
        REFERENCES items(tenant_id, company_id, id) ON DELETE RESTRICT,
    FOREIGN KEY (tenant_id, company_id, warehouse_id)
        REFERENCES warehouses(tenant_id, company_id, id) ON DELETE RESTRICT,
    FOREIGN KEY (tenant_id, company_id, transfer_id)
        REFERENCES inventory_transfers(tenant_id, company_id, id) ON DELETE RESTRICT
);
CREATE INDEX ix_inventory_serial_events_company_date
    ON inventory_serial_events(tenant_id, company_id, movement_date DESC, created_at DESC);
CREATE INDEX ix_inventory_serial_events_serial
    ON inventory_serial_events(tenant_id, company_id, serial_id, movement_date DESC);

CREATE OR REPLACE FUNCTION guard_inventory_serial_identity() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'Inventory serial identities are retained for audit history' USING ERRCODE = '23514';
    END IF;
    IF NEW.id IS DISTINCT FROM OLD.id
       OR NEW.tenant_id IS DISTINCT FROM OLD.tenant_id
       OR NEW.company_id IS DISTINCT FROM OLD.company_id
       OR NEW.item_id IS DISTINCT FROM OLD.item_id
       OR NEW.serial_number IS DISTINCT FROM OLD.serial_number
       OR NEW.first_received_movement_id IS DISTINCT FROM OLD.first_received_movement_id
       OR NEW.created_at IS DISTINCT FROM OLD.created_at THEN
        RAISE EXCEPTION 'Inventory serial identity fields are immutable' USING ERRCODE = '23514';
    END IF;
    NEW.updated_at := now();
    RETURN NEW;
END $$;

CREATE TRIGGER trg_inventory_serial_identity_immutable
    BEFORE UPDATE OR DELETE ON inventory_serial_numbers
    FOR EACH ROW EXECUTE FUNCTION guard_inventory_serial_identity();

CREATE OR REPLACE FUNCTION prevent_inventory_serial_event_mutation() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'Inventory serial events are immutable' USING ERRCODE = '23514';
END $$;

CREATE TRIGGER trg_inventory_serial_events_immutable
    BEFORE UPDATE OR DELETE ON inventory_serial_events
    FOR EACH ROW EXECUTE FUNCTION prevent_inventory_serial_event_mutation();

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

    IF movement_row.source_type = 'MANUAL' AND movement_row.movement_type = 'RECEIPT' THEN
        expected_event_type := 'RECEIPT';
    ELSIF movement_row.source_type = 'MANUAL' AND movement_row.movement_type = 'ISSUE' THEN
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

CREATE CONSTRAINT TRIGGER trg_inventory_serial_movement_complete
    AFTER INSERT ON inventory_movements
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION assert_inventory_serial_movement();

CREATE CONSTRAINT TRIGGER trg_inventory_serial_events_match_movement
    AFTER INSERT ON inventory_serial_events
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION assert_inventory_serial_movement();

CREATE OR REPLACE FUNCTION assert_inventory_serial_current_state() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    serial_key uuid;
    serial_row inventory_serial_numbers%ROWTYPE;
BEGIN
    IF TG_TABLE_NAME = 'inventory_serial_numbers' THEN
        serial_key := NEW.id;
    ELSE
        serial_key := NEW.serial_id;
    END IF;

    SELECT * INTO serial_row FROM inventory_serial_numbers WHERE id = serial_key;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Inventory serial identity missing for event' USING ERRCODE = '23514';
    END IF;

    IF serial_row.status = 'IN_STOCK' THEN
        IF serial_row.current_warehouse_id IS NULL OR NOT EXISTS (
            SELECT 1 FROM inventory_serial_events event
             WHERE event.tenant_id = serial_row.tenant_id AND event.company_id = serial_row.company_id
               AND event.serial_id = serial_row.id AND event.movement_id = serial_row.last_movement_id
               AND event.warehouse_id = serial_row.current_warehouse_id
               AND event.event_type IN ('RECEIPT', 'TRANSFER_IN')
        ) THEN
            RAISE EXCEPTION 'Serial in-stock location must match its last receipt/transfer event' USING ERRCODE = '23514';
        END IF;
    ELSE
        IF serial_row.current_warehouse_id IS NOT NULL OR NOT EXISTS (
            SELECT 1 FROM inventory_serial_events event
             WHERE event.tenant_id = serial_row.tenant_id AND event.company_id = serial_row.company_id
               AND event.serial_id = serial_row.id AND event.movement_id = serial_row.last_movement_id
               AND event.event_type = 'ISSUE'
        ) THEN
            RAISE EXCEPTION 'Issued serial state must match its last issue event' USING ERRCODE = '23514';
        END IF;
    END IF;
    RETURN NULL;
END $$;

CREATE CONSTRAINT TRIGGER trg_inventory_serial_number_current_state
    AFTER INSERT OR UPDATE ON inventory_serial_numbers
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION assert_inventory_serial_current_state();

CREATE CONSTRAINT TRIGGER trg_inventory_serial_event_current_state
    AFTER INSERT ON inventory_serial_events
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION assert_inventory_serial_current_state();

ALTER TABLE inventory_serial_numbers ENABLE ROW LEVEL SECURITY;
ALTER TABLE inventory_serial_numbers FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON inventory_serial_numbers
    USING (tenant_id = app_current_tenant_id())
    WITH CHECK (tenant_id = app_current_tenant_id());

ALTER TABLE inventory_serial_events ENABLE ROW LEVEL SECURITY;
ALTER TABLE inventory_serial_events FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON inventory_serial_events
    USING (tenant_id = app_current_tenant_id())
    WITH CHECK (tenant_id = app_current_tenant_id());

COMMENT ON TABLE inventory_serial_numbers IS
    'Current status/location of a physical serialized unit; identity is retained for audit history.';
COMMENT ON TABLE inventory_serial_events IS
    'Append-only serial history linked to the immutable inventory movement ledger.';

COMMIT;
