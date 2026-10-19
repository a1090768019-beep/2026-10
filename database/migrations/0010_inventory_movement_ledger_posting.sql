-- Link manual inventory receipts/issues to their posted double-entry journal.
BEGIN;

CREATE TABLE inventory_movement_accounting_postings (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL,
    company_id uuid NOT NULL,
    inventory_movement_id uuid NOT NULL,
    journal_entry_id uuid NOT NULL,
    inventory_asset_account_id uuid NOT NULL,
    counterpart_account_id uuid NOT NULL,
    movement_type varchar(16) NOT NULL CHECK (movement_type IN ('RECEIPT', 'ISSUE')),
    amount numeric(20,4) NOT NULL CHECK (amount > 0),
    posted_by uuid NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
    posted_at timestamptz NOT NULL DEFAULT now(),
    CHECK (inventory_asset_account_id <> counterpart_account_id),
    UNIQUE (tenant_id, company_id, id),
    UNIQUE (tenant_id, company_id, inventory_movement_id),
    UNIQUE (tenant_id, company_id, journal_entry_id),
    FOREIGN KEY (tenant_id, company_id) REFERENCES companies(tenant_id, id) ON DELETE RESTRICT,
    FOREIGN KEY (tenant_id, company_id, inventory_movement_id)
        REFERENCES inventory_movements(tenant_id, company_id, id) ON DELETE RESTRICT,
    FOREIGN KEY (tenant_id, company_id, journal_entry_id)
        REFERENCES journal_entries(tenant_id, company_id, id) ON DELETE RESTRICT,
    FOREIGN KEY (tenant_id, company_id, inventory_asset_account_id)
        REFERENCES accounts(tenant_id, company_id, id) ON DELETE RESTRICT,
    FOREIGN KEY (tenant_id, company_id, counterpart_account_id)
        REFERENCES accounts(tenant_id, company_id, id) ON DELETE RESTRICT
);
CREATE INDEX ix_inventory_movement_accounting_postings_posted_at
    ON inventory_movement_accounting_postings(tenant_id, company_id, posted_at DESC);

CREATE OR REPLACE FUNCTION prevent_inventory_movement_accounting_mutation() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'Inventory accounting links are immutable; use a reversal/correction'
        USING ERRCODE = '23514';
END $$;

CREATE TRIGGER trg_inventory_movement_accounting_immutable
    BEFORE UPDATE OR DELETE ON inventory_movement_accounting_postings
    FOR EACH ROW EXECUTE FUNCTION prevent_inventory_movement_accounting_mutation();

CREATE OR REPLACE FUNCTION assert_inventory_movement_accounting_link() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    movement_row inventory_movements%ROWTYPE;
    entry_row journal_entries%ROWTYPE;
    line_count integer;
    total_debit numeric(30,4);
    total_credit numeric(30,4);
BEGIN
    SELECT * INTO movement_row
      FROM inventory_movements
     WHERE tenant_id = NEW.tenant_id
       AND company_id = NEW.company_id
       AND id = NEW.inventory_movement_id;

    IF NOT FOUND
       OR movement_row.source_type <> 'MANUAL'
       OR movement_row.transfer_id IS NOT NULL
       OR movement_row.movement_type IS DISTINCT FROM NEW.movement_type
       OR abs(movement_row.inventory_value_delta) IS DISTINCT FROM NEW.amount
       OR NEW.amount <= 0 THEN
        RAISE EXCEPTION 'Accounting link must match a valued manual inventory receipt or issue'
            USING ERRCODE = '23514';
    END IF;

    SELECT * INTO entry_row
      FROM journal_entries
     WHERE tenant_id = NEW.tenant_id
       AND company_id = NEW.company_id
       AND id = NEW.journal_entry_id;

    IF NOT FOUND
       OR entry_row.status <> 'POSTED'
       OR entry_row.source_type <> 'INVENTORY_MOVEMENT'
       OR entry_row.source_id IS DISTINCT FROM movement_row.id
       OR entry_row.entry_date IS DISTINCT FROM movement_row.movement_date
       OR entry_row.posted_at IS NULL THEN
        RAISE EXCEPTION 'Inventory accounting link must reference the posted journal for this movement'
            USING ERRCODE = '23514';
    END IF;

    SELECT count(*)::integer, COALESCE(sum(debit), 0), COALESCE(sum(credit), 0)
      INTO line_count, total_debit, total_credit
      FROM journal_entry_lines
     WHERE tenant_id = NEW.tenant_id
       AND company_id = NEW.company_id
       AND journal_entry_id = NEW.journal_entry_id;

    IF line_count <> 2 OR total_debit <> NEW.amount OR total_credit <> NEW.amount THEN
        RAISE EXCEPTION 'Inventory movement journal must contain exactly two balanced lines for its stock value'
            USING ERRCODE = '23514';
    END IF;

    IF NEW.movement_type = 'RECEIPT' THEN
        IF NOT EXISTS (
            SELECT 1 FROM journal_entry_lines
             WHERE tenant_id = NEW.tenant_id AND company_id = NEW.company_id
               AND journal_entry_id = NEW.journal_entry_id
               AND account_id = NEW.inventory_asset_account_id AND debit = NEW.amount AND credit = 0
        ) OR NOT EXISTS (
            SELECT 1 FROM journal_entry_lines
             WHERE tenant_id = NEW.tenant_id AND company_id = NEW.company_id
               AND journal_entry_id = NEW.journal_entry_id
               AND account_id = NEW.counterpart_account_id AND debit = 0 AND credit = NEW.amount
        ) THEN
            RAISE EXCEPTION 'Receipt accounting must debit inventory and credit the selected counterpart account'
                USING ERRCODE = '23514';
        END IF;
    ELSE
        IF NOT EXISTS (
            SELECT 1 FROM journal_entry_lines
             WHERE tenant_id = NEW.tenant_id AND company_id = NEW.company_id
               AND journal_entry_id = NEW.journal_entry_id
               AND account_id = NEW.counterpart_account_id AND debit = NEW.amount AND credit = 0
        ) OR NOT EXISTS (
            SELECT 1 FROM journal_entry_lines
             WHERE tenant_id = NEW.tenant_id AND company_id = NEW.company_id
               AND journal_entry_id = NEW.journal_entry_id
               AND account_id = NEW.inventory_asset_account_id AND debit = 0 AND credit = NEW.amount
        ) THEN
            RAISE EXCEPTION 'Issue accounting must debit the expense/counterpart account and credit inventory'
                USING ERRCODE = '23514';
        END IF;
    END IF;

    RETURN NULL;
END $$;

CREATE CONSTRAINT TRIGGER trg_inventory_movement_accounting_link_valid
    AFTER INSERT ON inventory_movement_accounting_postings
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION assert_inventory_movement_accounting_link();

ALTER TABLE inventory_movement_accounting_postings ENABLE ROW LEVEL SECURITY;
ALTER TABLE inventory_movement_accounting_postings FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON inventory_movement_accounting_postings
    USING (tenant_id = app_current_tenant_id())
    WITH CHECK (tenant_id = app_current_tenant_id());

COMMENT ON TABLE inventory_movement_accounting_postings IS
    'Immutable link from a valued manual inventory receipt/issue to its one-time posted double-entry journal.';

COMMIT;
