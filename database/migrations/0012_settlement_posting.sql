-- Customer receipts and supplier payments with invoice allocation and internal GL posting links.
-- These records represent cash settlement, not bank reconciliation or external payment processing.
BEGIN;

CREATE TABLE customer_receipts (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL,
    company_id uuid NOT NULL,
    customer_id uuid NOT NULL,
    receipt_number varchar(80) NOT NULL CHECK (length(btrim(receipt_number)) > 0 AND receipt_number = upper(receipt_number)),
    receipt_date date NOT NULL,
    currency_code char(3) NOT NULL,
    amount numeric(20,2) NOT NULL CHECK (amount > 0),
    notes varchar(2000) NOT NULL DEFAULT '',
    status varchar(20) NOT NULL DEFAULT 'DRAFT' CHECK (status IN ('DRAFT','ACCOUNTING_POSTED')),
    accounting_posted_journal_entry_id uuid NULL,
    accounting_posted_at timestamptz NULL,
    created_by uuid NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, company_id, id),
    UNIQUE (tenant_id, company_id, receipt_number),
    FOREIGN KEY (tenant_id, company_id) REFERENCES companies(tenant_id, id) ON DELETE RESTRICT,
    FOREIGN KEY (tenant_id, company_id, customer_id) REFERENCES customers(tenant_id, company_id, id) ON DELETE RESTRICT,
    FOREIGN KEY (tenant_id, company_id, accounting_posted_journal_entry_id)
        REFERENCES journal_entries(tenant_id, company_id, id) ON DELETE RESTRICT,
    CHECK (
        (status = 'DRAFT' AND accounting_posted_journal_entry_id IS NULL AND accounting_posted_at IS NULL)
        OR
        (status = 'ACCOUNTING_POSTED' AND accounting_posted_journal_entry_id IS NOT NULL AND accounting_posted_at IS NOT NULL)
    )
);
CREATE INDEX ix_customer_receipts_customer_date
    ON customer_receipts(tenant_id, company_id, customer_id, receipt_date DESC);
CREATE UNIQUE INDEX ux_customer_receipts_posted_journal
    ON customer_receipts(tenant_id, company_id, accounting_posted_journal_entry_id)
    WHERE accounting_posted_journal_entry_id IS NOT NULL;

CREATE TABLE customer_receipt_allocations (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL,
    company_id uuid NOT NULL,
    receipt_id uuid NOT NULL,
    sales_invoice_draft_id uuid NOT NULL,
    allocated_amount numeric(20,2) NOT NULL CHECK (allocated_amount > 0),
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, company_id, id),
    UNIQUE (tenant_id, company_id, receipt_id, sales_invoice_draft_id),
    FOREIGN KEY (tenant_id, company_id, receipt_id)
        REFERENCES customer_receipts(tenant_id, company_id, id) ON DELETE RESTRICT,
    FOREIGN KEY (tenant_id, company_id, sales_invoice_draft_id)
        REFERENCES sales_invoice_drafts(tenant_id, company_id, id) ON DELETE RESTRICT
);
CREATE INDEX ix_customer_receipt_allocations_invoice
    ON customer_receipt_allocations(tenant_id, company_id, sales_invoice_draft_id);

CREATE TABLE supplier_payments (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL,
    company_id uuid NOT NULL,
    supplier_id uuid NOT NULL,
    payment_number varchar(80) NOT NULL CHECK (length(btrim(payment_number)) > 0 AND payment_number = upper(payment_number)),
    payment_date date NOT NULL,
    currency_code char(3) NOT NULL,
    amount numeric(20,2) NOT NULL CHECK (amount > 0),
    notes varchar(2000) NOT NULL DEFAULT '',
    status varchar(20) NOT NULL DEFAULT 'DRAFT' CHECK (status IN ('DRAFT','ACCOUNTING_POSTED')),
    accounting_posted_journal_entry_id uuid NULL,
    accounting_posted_at timestamptz NULL,
    created_by uuid NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, company_id, id),
    UNIQUE (tenant_id, company_id, payment_number),
    FOREIGN KEY (tenant_id, company_id) REFERENCES companies(tenant_id, id) ON DELETE RESTRICT,
    FOREIGN KEY (tenant_id, company_id, supplier_id) REFERENCES suppliers(tenant_id, company_id, id) ON DELETE RESTRICT,
    FOREIGN KEY (tenant_id, company_id, accounting_posted_journal_entry_id)
        REFERENCES journal_entries(tenant_id, company_id, id) ON DELETE RESTRICT,
    CHECK (
        (status = 'DRAFT' AND accounting_posted_journal_entry_id IS NULL AND accounting_posted_at IS NULL)
        OR
        (status = 'ACCOUNTING_POSTED' AND accounting_posted_journal_entry_id IS NOT NULL AND accounting_posted_at IS NOT NULL)
    )
);
CREATE INDEX ix_supplier_payments_supplier_date
    ON supplier_payments(tenant_id, company_id, supplier_id, payment_date DESC);
CREATE UNIQUE INDEX ux_supplier_payments_posted_journal
    ON supplier_payments(tenant_id, company_id, accounting_posted_journal_entry_id)
    WHERE accounting_posted_journal_entry_id IS NOT NULL;

CREATE TABLE supplier_payment_allocations (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL,
    company_id uuid NOT NULL,
    payment_id uuid NOT NULL,
    purchase_invoice_draft_id uuid NOT NULL,
    allocated_amount numeric(20,2) NOT NULL CHECK (allocated_amount > 0),
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, company_id, id),
    UNIQUE (tenant_id, company_id, payment_id, purchase_invoice_draft_id),
    FOREIGN KEY (tenant_id, company_id, payment_id)
        REFERENCES supplier_payments(tenant_id, company_id, id) ON DELETE RESTRICT,
    FOREIGN KEY (tenant_id, company_id, purchase_invoice_draft_id)
        REFERENCES purchase_invoice_drafts(tenant_id, company_id, id) ON DELETE RESTRICT
);
CREATE INDEX ix_supplier_payment_allocations_invoice
    ON supplier_payment_allocations(tenant_id, company_id, purchase_invoice_draft_id);

CREATE OR REPLACE FUNCTION guard_customer_receipt_allocation() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    receipt_row customer_receipts%ROWTYPE;
    invoice_customer_id uuid;
    invoice_status varchar(20);
BEGIN
    IF TG_OP = 'DELETE' THEN
        SELECT * INTO receipt_row FROM customer_receipts
         WHERE tenant_id = OLD.tenant_id AND company_id = OLD.company_id AND id = OLD.receipt_id
         FOR UPDATE;
        IF NOT FOUND OR receipt_row.status <> 'DRAFT' THEN
            RAISE EXCEPTION 'Allocations can be changed only while the receipt is a draft' USING ERRCODE = '23514';
        END IF;
        RETURN OLD;
    END IF;

    SELECT * INTO receipt_row FROM customer_receipts
     WHERE tenant_id = NEW.tenant_id AND company_id = NEW.company_id AND id = NEW.receipt_id
     FOR UPDATE;
    IF NOT FOUND OR receipt_row.status <> 'DRAFT' THEN
        RAISE EXCEPTION 'Allocations can be changed only while the receipt is a draft' USING ERRCODE = '23514';
    END IF;

    SELECT customer_id, status INTO invoice_customer_id, invoice_status
      FROM sales_invoice_drafts
     WHERE tenant_id = NEW.tenant_id AND company_id = NEW.company_id AND id = NEW.sales_invoice_draft_id;
    IF NOT FOUND OR invoice_status <> 'ACCOUNTING_POSTED' OR invoice_customer_id <> receipt_row.customer_id THEN
        RAISE EXCEPTION 'Receipt allocation must target a posted invoice for the same customer' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER trg_customer_receipt_allocation_guard
    BEFORE INSERT OR UPDATE OR DELETE ON customer_receipt_allocations
    FOR EACH ROW EXECUTE FUNCTION guard_customer_receipt_allocation();

CREATE OR REPLACE FUNCTION guard_supplier_payment_allocation() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    payment_row supplier_payments%ROWTYPE;
    invoice_supplier_id uuid;
    invoice_status varchar(20);
BEGIN
    IF TG_OP = 'DELETE' THEN
        SELECT * INTO payment_row FROM supplier_payments
         WHERE tenant_id = OLD.tenant_id AND company_id = OLD.company_id AND id = OLD.payment_id
         FOR UPDATE;
        IF NOT FOUND OR payment_row.status <> 'DRAFT' THEN
            RAISE EXCEPTION 'Allocations can be changed only while the payment is a draft' USING ERRCODE = '23514';
        END IF;
        RETURN OLD;
    END IF;

    SELECT * INTO payment_row FROM supplier_payments
     WHERE tenant_id = NEW.tenant_id AND company_id = NEW.company_id AND id = NEW.payment_id
     FOR UPDATE;
    IF NOT FOUND OR payment_row.status <> 'DRAFT' THEN
        RAISE EXCEPTION 'Allocations can be changed only while the payment is a draft' USING ERRCODE = '23514';
    END IF;

    SELECT supplier_id, status INTO invoice_supplier_id, invoice_status
      FROM purchase_invoice_drafts
     WHERE tenant_id = NEW.tenant_id AND company_id = NEW.company_id AND id = NEW.purchase_invoice_draft_id;
    IF NOT FOUND OR invoice_status <> 'ACCOUNTING_POSTED' OR invoice_supplier_id <> payment_row.supplier_id THEN
        RAISE EXCEPTION 'Payment allocation must target a posted invoice for the same supplier' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER trg_supplier_payment_allocation_guard
    BEFORE INSERT OR UPDATE OR DELETE ON supplier_payment_allocations
    FOR EACH ROW EXECUTE FUNCTION guard_supplier_payment_allocation();

-- Validate source-document allocation totals in the database, independent of API validation.
CREATE OR REPLACE FUNCTION validate_customer_receipt_allocation_totals() RETURNS trigger
LANGUAGE plpgsql AS $
DECLARE
    source_id uuid;
    source_amount numeric(20,2);
    source_status varchar(20);
    allocated_total numeric(20,2);
BEGIN
    source_id := CASE WHEN TG_OP = 'DELETE' THEN OLD.receipt_id ELSE NEW.receipt_id END;
    SELECT amount, status INTO source_amount, source_status
      FROM customer_receipts
     WHERE tenant_id = CASE WHEN TG_OP = 'DELETE' THEN OLD.tenant_id ELSE NEW.tenant_id END
       AND company_id = CASE WHEN TG_OP = 'DELETE' THEN OLD.company_id ELSE NEW.company_id END
       AND id = source_id
     FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'Receipt not found' USING ERRCODE = '23503'; END IF;
    SELECT COALESCE(sum(allocated_amount),0) INTO allocated_total
      FROM customer_receipt_allocations
     WHERE tenant_id = CASE WHEN TG_OP = 'DELETE' THEN OLD.tenant_id ELSE NEW.tenant_id END
       AND company_id = CASE WHEN TG_OP = 'DELETE' THEN OLD.company_id ELSE NEW.company_id END
       AND receipt_id = source_id;
    IF allocated_total > source_amount THEN
        RAISE EXCEPTION 'Receipt allocations cannot exceed receipt amount' USING ERRCODE = '23514';
    END IF;
    IF TG_OP = 'DELETE' THEN RETURN OLD; ELSE RETURN NEW; END IF;
END $;
CREATE CONSTRAINT TRIGGER trg_customer_receipt_allocation_total
    AFTER INSERT OR UPDATE OR DELETE ON customer_receipt_allocations
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW
    EXECUTE FUNCTION validate_customer_receipt_allocation_totals();

CREATE OR REPLACE FUNCTION validate_supplier_payment_allocation_totals() RETURNS trigger
LANGUAGE plpgsql AS $
DECLARE
    source_id uuid;
    source_amount numeric(20,2);
    source_status varchar(20);
    allocated_total numeric(20,2);
BEGIN
    source_id := CASE WHEN TG_OP = 'DELETE' THEN OLD.payment_id ELSE NEW.payment_id END;
    SELECT amount, status INTO source_amount, source_status
      FROM supplier_payments
     WHERE tenant_id = CASE WHEN TG_OP = 'DELETE' THEN OLD.tenant_id ELSE NEW.tenant_id END
       AND company_id = CASE WHEN TG_OP = 'DELETE' THEN OLD.company_id ELSE NEW.company_id END
       AND id = source_id
     FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'Supplier payment not found' USING ERRCODE = '23503'; END IF;
    SELECT COALESCE(sum(allocated_amount),0) INTO allocated_total
      FROM supplier_payment_allocations
     WHERE tenant_id = CASE WHEN TG_OP = 'DELETE' THEN OLD.tenant_id ELSE NEW.tenant_id END
       AND company_id = CASE WHEN TG_OP = 'DELETE' THEN OLD.company_id ELSE NEW.company_id END
       AND payment_id = source_id;
    IF allocated_total > source_amount THEN
        RAISE EXCEPTION 'Payment allocations cannot exceed payment amount' USING ERRCODE = '23514';
    END IF;
    RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
END $;
CREATE CONSTRAINT TRIGGER trg_supplier_payment_allocation_total
    AFTER INSERT OR UPDATE OR DELETE ON supplier_payment_allocations
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW
    EXECUTE FUNCTION validate_supplier_payment_allocation_totals();

CREATE OR REPLACE FUNCTION guard_customer_receipt_mutation() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    linked_source_type varchar(64);
    linked_source_id uuid;
    linked_status varchar(20);
    linked_posted_at timestamptz;
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'Receipts are retained; use a correcting journal workflow' USING ERRCODE = '23514';
    END IF;
    IF OLD.status = 'ACCOUNTING_POSTED' THEN
        RAISE EXCEPTION 'Accounting-posted receipts are immutable' USING ERRCODE = '23514';
    END IF;

    IF NEW.status = 'ACCOUNTING_POSTED' THEN
        IF NEW.accounting_posted_journal_entry_id IS NULL OR NEW.accounting_posted_at IS NULL THEN
            RAISE EXCEPTION 'A posted receipt must link to a posted journal' USING ERRCODE = '23514';
        END IF;
        IF (to_jsonb(NEW) - ARRAY['status','accounting_posted_journal_entry_id','accounting_posted_at'])
           IS DISTINCT FROM
           (to_jsonb(OLD) - ARRAY['status','accounting_posted_journal_entry_id','accounting_posted_at']) THEN
            RAISE EXCEPTION 'Posting cannot change receipt commercial data' USING ERRCODE = '23514';
        END IF;
        IF (SELECT COALESCE(sum(allocated_amount),0) FROM customer_receipt_allocations
             WHERE tenant_id=NEW.tenant_id AND company_id=NEW.company_id AND receipt_id=NEW.id) <> NEW.amount THEN
            RAISE EXCEPTION 'Receipt allocations must equal the receipt amount before posting' USING ERRCODE = '23514';
        END IF;
        IF EXISTS (
            SELECT 1
              FROM customer_receipt_allocations a
              JOIN sales_invoice_drafts i ON i.tenant_id=a.tenant_id AND i.company_id=a.company_id AND i.id=a.sales_invoice_draft_id
             WHERE a.tenant_id=NEW.tenant_id AND a.company_id=NEW.company_id AND a.receipt_id=NEW.id
               AND COALESCE((SELECT sum(a2.allocated_amount)
                               FROM customer_receipt_allocations a2
                               JOIN customer_receipts r2 ON r2.tenant_id=a2.tenant_id AND r2.company_id=a2.company_id AND r2.id=a2.receipt_id
                              WHERE a2.tenant_id=a.tenant_id AND a2.company_id=a.company_id
                                AND a2.sales_invoice_draft_id=a.sales_invoice_draft_id
                                AND r2.status='ACCOUNTING_POSTED' AND r2.id<>NEW.id),0) + a.allocated_amount > i.total_amount
        ) THEN
            RAISE EXCEPTION 'Receipt allocation exceeds the invoice remaining balance' USING ERRCODE = '23514';
        END IF;
        SELECT source_type, source_id, status, posted_at
          INTO linked_source_type, linked_source_id, linked_status, linked_posted_at
          FROM journal_entries
         WHERE tenant_id=NEW.tenant_id AND company_id=NEW.company_id AND id=NEW.accounting_posted_journal_entry_id;
        IF NOT FOUND OR linked_source_type IS DISTINCT FROM 'CUSTOMER_RECEIPT'
           OR linked_source_id IS DISTINCT FROM NEW.id OR linked_status IS DISTINCT FROM 'POSTED'
           OR linked_posted_at IS NULL THEN
            RAISE EXCEPTION 'Receipt link must reference its own posted journal entry' USING ERRCODE = '23514';
        END IF;
    ELSIF NEW.status <> 'DRAFT' OR NEW.accounting_posted_journal_entry_id IS NOT NULL OR NEW.accounting_posted_at IS NOT NULL THEN
        RAISE EXCEPTION 'Invalid customer receipt status/link transition' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER trg_customer_receipt_mutation
    BEFORE UPDATE OR DELETE ON customer_receipts
    FOR EACH ROW EXECUTE FUNCTION guard_customer_receipt_mutation();

CREATE OR REPLACE FUNCTION guard_supplier_payment_mutation() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    linked_source_type varchar(64);
    linked_source_id uuid;
    linked_status varchar(20);
    linked_posted_at timestamptz;
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'Supplier payments are retained; use a correcting journal workflow' USING ERRCODE = '23514';
    END IF;
    IF OLD.status = 'ACCOUNTING_POSTED' THEN
        RAISE EXCEPTION 'Accounting-posted supplier payments are immutable' USING ERRCODE = '23514';
    END IF;

    IF NEW.status = 'ACCOUNTING_POSTED' THEN
        IF NEW.accounting_posted_journal_entry_id IS NULL OR NEW.accounting_posted_at IS NULL THEN
            RAISE EXCEPTION 'A posted supplier payment must link to a posted journal' USING ERRCODE = '23514';
        END IF;
        IF (to_jsonb(NEW) - ARRAY['status','accounting_posted_journal_entry_id','accounting_posted_at'])
           IS DISTINCT FROM
           (to_jsonb(OLD) - ARRAY['status','accounting_posted_journal_entry_id','accounting_posted_at']) THEN
            RAISE EXCEPTION 'Posting cannot change supplier payment commercial data' USING ERRCODE = '23514';
        END IF;
        IF (SELECT COALESCE(sum(allocated_amount),0) FROM supplier_payment_allocations
             WHERE tenant_id=NEW.tenant_id AND company_id=NEW.company_id AND payment_id=NEW.id) <> NEW.amount THEN
            RAISE EXCEPTION 'Payment allocations must equal the payment amount before posting' USING ERRCODE = '23514';
        END IF;
        IF EXISTS (
            SELECT 1
              FROM supplier_payment_allocations a
              JOIN purchase_invoice_drafts i ON i.tenant_id=a.tenant_id AND i.company_id=a.company_id AND i.id=a.purchase_invoice_draft_id
             WHERE a.tenant_id=NEW.tenant_id AND a.company_id=NEW.company_id AND a.payment_id=NEW.id
               AND COALESCE((SELECT sum(a2.allocated_amount)
                               FROM supplier_payment_allocations a2
                               JOIN supplier_payments p2 ON p2.tenant_id=a2.tenant_id AND p2.company_id=a2.company_id AND p2.id=a2.payment_id
                              WHERE a2.tenant_id=a.tenant_id AND a2.company_id=a.company_id
                                AND a2.purchase_invoice_draft_id=a.purchase_invoice_draft_id
                                AND p2.status='ACCOUNTING_POSTED' AND p2.id<>NEW.id),0) + a.allocated_amount > i.total_amount
        ) THEN
            RAISE EXCEPTION 'Payment allocation exceeds the invoice remaining balance' USING ERRCODE = '23514';
        END IF;
        SELECT source_type, source_id, status, posted_at
          INTO linked_source_type, linked_source_id, linked_status, linked_posted_at
          FROM journal_entries
         WHERE tenant_id=NEW.tenant_id AND company_id=NEW.company_id AND id=NEW.accounting_posted_journal_entry_id;
        IF NOT FOUND OR linked_source_type IS DISTINCT FROM 'SUPPLIER_PAYMENT'
           OR linked_source_id IS DISTINCT FROM NEW.id OR linked_status IS DISTINCT FROM 'POSTED'
           OR linked_posted_at IS NULL THEN
            RAISE EXCEPTION 'Supplier payment link must reference its own posted journal entry' USING ERRCODE = '23514';
        END IF;
    ELSIF NEW.status <> 'DRAFT' OR NEW.accounting_posted_journal_entry_id IS NOT NULL OR NEW.accounting_posted_at IS NOT NULL THEN
        RAISE EXCEPTION 'Invalid supplier payment status/link transition' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER trg_supplier_payment_mutation
    BEFORE UPDATE OR DELETE ON supplier_payments
    FOR EACH ROW EXECUTE FUNCTION guard_supplier_payment_mutation();

ALTER TABLE customer_receipts ENABLE ROW LEVEL SECURITY;
ALTER TABLE customer_receipts FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON customer_receipts
    USING (tenant_id = app_current_tenant_id()) WITH CHECK (tenant_id = app_current_tenant_id());
ALTER TABLE customer_receipt_allocations ENABLE ROW LEVEL SECURITY;
ALTER TABLE customer_receipt_allocations FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON customer_receipt_allocations
    USING (tenant_id = app_current_tenant_id()) WITH CHECK (tenant_id = app_current_tenant_id());
ALTER TABLE supplier_payments ENABLE ROW LEVEL SECURITY;
ALTER TABLE supplier_payments FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON supplier_payments
    USING (tenant_id = app_current_tenant_id()) WITH CHECK (tenant_id = app_current_tenant_id());
ALTER TABLE supplier_payment_allocations ENABLE ROW LEVEL SECURITY;
ALTER TABLE supplier_payment_allocations FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON supplier_payment_allocations
    USING (tenant_id = app_current_tenant_id()) WITH CHECK (tenant_id = app_current_tenant_id());

COMMENT ON TABLE customer_receipts IS 'Customer cash receipt records; posting creates an internal GL journal and allocates the receipt to posted sales drafts.';
COMMENT ON TABLE supplier_payments IS 'Supplier cash payment records; posting creates an internal GL journal and allocates payment to posted purchase drafts.';

COMMIT;
