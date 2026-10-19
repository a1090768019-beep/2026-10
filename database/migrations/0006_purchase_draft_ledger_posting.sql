-- Internal double-entry posting linkage for VAT-calculated purchase drafts.
-- This is bookkeeping only; it does not establish legal invoice validity or input VAT recoverability.
BEGIN;

ALTER TABLE purchase_invoice_drafts
    DROP CONSTRAINT IF EXISTS purchase_invoice_drafts_status_check,
    ADD COLUMN accounting_posted_journal_entry_id uuid NULL,
    ADD COLUMN accounting_posted_at timestamptz NULL;

ALTER TABLE purchase_invoice_drafts
    ADD CONSTRAINT ck_purchase_invoice_draft_status
        CHECK (status IN ('DRAFT', 'ACCOUNTING_POSTED')),
    ADD CONSTRAINT ck_purchase_invoice_draft_accounting_link
        CHECK (
            (status = 'DRAFT' AND accounting_posted_journal_entry_id IS NULL AND accounting_posted_at IS NULL)
            OR
            (status = 'ACCOUNTING_POSTED' AND accounting_posted_journal_entry_id IS NOT NULL AND accounting_posted_at IS NOT NULL)
        ),
    ADD CONSTRAINT fk_purchase_invoice_draft_posted_journal
        FOREIGN KEY (tenant_id, company_id, accounting_posted_journal_entry_id)
        REFERENCES journal_entries(tenant_id, company_id, id) ON DELETE RESTRICT;

CREATE UNIQUE INDEX ux_purchase_invoice_drafts_posted_journal
    ON purchase_invoice_drafts(tenant_id, company_id, accounting_posted_journal_entry_id)
    WHERE accounting_posted_journal_entry_id IS NOT NULL;

CREATE OR REPLACE FUNCTION guard_purchase_invoice_draft_header_mutation() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    linked_source_type varchar(64);
    linked_source_id uuid;
    linked_status varchar(20);
    linked_posted_at timestamptz;
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'Purchase invoice drafts are retained; use an approved correction workflow'
            USING ERRCODE = '23514';
    END IF;

    IF OLD.status = 'ACCOUNTING_POSTED' THEN
        RAISE EXCEPTION 'Accounting-posted purchase drafts are immutable'
            USING ERRCODE = '23514';
    END IF;

    IF NEW.status = 'ACCOUNTING_POSTED' THEN
        IF OLD.status <> 'DRAFT'
           OR NEW.accounting_posted_journal_entry_id IS NULL
           OR NEW.accounting_posted_at IS NULL THEN
            RAISE EXCEPTION 'A purchase draft can transition to ACCOUNTING_POSTED only with a journal link'
                USING ERRCODE = '23514';
        END IF;

        IF (to_jsonb(NEW) - ARRAY['status', 'accounting_posted_journal_entry_id', 'accounting_posted_at'])
           IS DISTINCT FROM
           (to_jsonb(OLD) - ARRAY['status', 'accounting_posted_journal_entry_id', 'accounting_posted_at']) THEN
            RAISE EXCEPTION 'Posting a purchase draft cannot modify commercial or tax data'
                USING ERRCODE = '23514';
        END IF;

        SELECT source_type, source_id, status, posted_at
          INTO linked_source_type, linked_source_id, linked_status, linked_posted_at
          FROM journal_entries
         WHERE tenant_id = NEW.tenant_id
           AND company_id = NEW.company_id
           AND id = NEW.accounting_posted_journal_entry_id;

        IF NOT FOUND
           OR linked_source_type IS DISTINCT FROM 'PURCHASE_INVOICE_DRAFT'
           OR linked_source_id IS DISTINCT FROM NEW.id
           OR linked_status IS DISTINCT FROM 'POSTED'
           OR linked_posted_at IS NULL THEN
            RAISE EXCEPTION 'Purchase draft accounting link must reference its own posted journal entry'
                USING ERRCODE = '23514';
        END IF;
    ELSE
        IF NEW.status <> 'DRAFT'
           OR NEW.accounting_posted_journal_entry_id IS NOT NULL
           OR NEW.accounting_posted_at IS NOT NULL THEN
            RAISE EXCEPTION 'Invalid purchase draft status/link transition'
                USING ERRCODE = '23514';
        END IF;
    END IF;

    RETURN NEW;
END $$;

CREATE TRIGGER trg_purchase_invoice_draft_header_mutation
    BEFORE UPDATE OR DELETE ON purchase_invoice_drafts
    FOR EACH ROW EXECUTE FUNCTION guard_purchase_invoice_draft_header_mutation();

CREATE OR REPLACE FUNCTION guard_purchase_invoice_draft_line_mutation() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    parent_status varchar(20);
    target_tenant uuid;
    target_company uuid;
    target_invoice uuid;
BEGIN
    IF TG_OP = 'INSERT' THEN
        target_tenant := NEW.tenant_id;
        target_company := NEW.company_id;
        target_invoice := NEW.purchase_invoice_draft_id;
    ELSE
        target_tenant := OLD.tenant_id;
        target_company := OLD.company_id;
        target_invoice := OLD.purchase_invoice_draft_id;
    END IF;

    IF TG_OP = 'UPDATE'
       AND (NEW.tenant_id <> OLD.tenant_id
         OR NEW.company_id <> OLD.company_id
         OR NEW.purchase_invoice_draft_id <> OLD.purchase_invoice_draft_id
         OR NEW.line_number <> OLD.line_number) THEN
        RAISE EXCEPTION 'Purchase draft line identity cannot be changed'
            USING ERRCODE = '23514';
    END IF;

    SELECT status INTO parent_status
      FROM purchase_invoice_drafts
     WHERE tenant_id = target_tenant
       AND company_id = target_company
       AND id = target_invoice
     FOR UPDATE;

    IF NOT FOUND OR parent_status <> 'DRAFT' THEN
        RAISE EXCEPTION 'Purchase draft lines can be changed only while the parent is a draft'
            USING ERRCODE = '23514';
    END IF;

    IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
    RETURN NEW;
END $$;

CREATE TRIGGER trg_purchase_invoice_draft_line_mutation
    BEFORE INSERT OR UPDATE OR DELETE ON purchase_invoice_draft_lines
    FOR EACH ROW EXECUTE FUNCTION guard_purchase_invoice_draft_line_mutation();

COMMENT ON COLUMN purchase_invoice_drafts.accounting_posted_journal_entry_id IS
    'Internal double-entry GL posting link only; not a legal invoice or tax authority clearance.';
COMMENT ON COLUMN purchase_invoice_drafts.accounting_posted_at IS
    'Timestamp of internal GL posting; input VAT recoverability must be reviewed independently.';
COMMIT;
