-- Incremental migration: tenant-scoped customer master and immutable sales invoice drafts.
-- Apply once, after 0001_initial.sql, using the schema-migration owner.
BEGIN;

CREATE TABLE customers (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL,
    company_id uuid NOT NULL,
    customer_code varchar(64) NOT NULL CHECK (length(btrim(customer_code)) > 0 AND customer_code = upper(customer_code)),
    name varchar(200) NOT NULL CHECK (length(btrim(name)) > 0),
    email varchar(320) NULL,
    phone varchar(32) NULL,
    tax_number varchar(32) NULL,
    is_active boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, company_id, id),
    UNIQUE (tenant_id, company_id, customer_code),
    FOREIGN KEY (tenant_id, company_id)
        REFERENCES companies(tenant_id, id) ON DELETE RESTRICT
);
CREATE INDEX ix_customers_tenant_company_name ON customers(tenant_id, company_id, name);

CREATE TABLE sales_invoice_drafts (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL,
    company_id uuid NOT NULL,
    customer_id uuid NOT NULL,
    invoice_number varchar(80) NOT NULL
        CHECK (length(btrim(invoice_number)) > 0 AND invoice_number = upper(invoice_number)),
    issue_date date NOT NULL,
    due_date date NULL,
    currency_code char(3) NOT NULL,
    customer_name_snapshot varchar(200) NOT NULL,
    customer_tax_number_snapshot varchar(32) NULL,
    subtotal_amount numeric(20,4) NOT NULL CHECK (subtotal_amount >= 0),
    notes varchar(2000) NOT NULL DEFAULT '',
    status varchar(20) NOT NULL DEFAULT 'DRAFT' CHECK (status = 'DRAFT'),
    created_by uuid NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
    created_at timestamptz NOT NULL DEFAULT now(),
    CHECK (due_date IS NULL OR due_date >= issue_date),
    UNIQUE (tenant_id, company_id, id),
    UNIQUE (tenant_id, company_id, invoice_number),
    FOREIGN KEY (tenant_id, company_id)
        REFERENCES companies(tenant_id, id) ON DELETE RESTRICT,
    FOREIGN KEY (tenant_id, company_id, customer_id)
        REFERENCES customers(tenant_id, company_id, id) ON DELETE RESTRICT
);
CREATE INDEX ix_sales_invoice_drafts_customer_date
    ON sales_invoice_drafts(tenant_id, company_id, customer_id, issue_date DESC);

CREATE TABLE sales_invoice_draft_lines (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL,
    company_id uuid NOT NULL,
    sales_invoice_draft_id uuid NOT NULL,
    line_number integer NOT NULL CHECK (line_number > 0),
    description varchar(500) NOT NULL CHECK (length(btrim(description)) > 0),
    quantity numeric(20,4) NOT NULL CHECK (quantity > 0),
    unit_price numeric(20,4) NOT NULL CHECK (unit_price >= 0),
    line_amount numeric(20,4) NOT NULL CHECK (line_amount >= 0),
    CHECK (line_amount = round(quantity * unit_price, 4)),
    UNIQUE (tenant_id, company_id, sales_invoice_draft_id, line_number),
    FOREIGN KEY (tenant_id, company_id, sales_invoice_draft_id)
        REFERENCES sales_invoice_drafts(tenant_id, company_id, id) ON DELETE RESTRICT
);
CREATE INDEX ix_sales_invoice_draft_lines_invoice
    ON sales_invoice_draft_lines(tenant_id, company_id, sales_invoice_draft_id, line_number);

-- Deferred checks see the header and all its lines after the transaction has populated them.
-- This prevents a committed draft with no lines or a subtotal that differs from its line sum.
CREATE OR REPLACE FUNCTION assert_sales_invoice_draft_totals() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    invoice_id uuid;
    invoice_tenant_id uuid;
    invoice_company_id uuid;
    expected_subtotal numeric(20,4);
    actual_subtotal numeric;
    actual_line_count integer;
BEGIN
    IF TG_TABLE_NAME = 'sales_invoice_drafts' THEN
        invoice_id := NEW.id;
        invoice_tenant_id := NEW.tenant_id;
        invoice_company_id := NEW.company_id;
    ELSIF TG_OP = 'DELETE' THEN
        invoice_id := OLD.sales_invoice_draft_id;
        invoice_tenant_id := OLD.tenant_id;
        invoice_company_id := OLD.company_id;
    ELSE
        invoice_id := NEW.sales_invoice_draft_id;
        invoice_tenant_id := NEW.tenant_id;
        invoice_company_id := NEW.company_id;
    END IF;

    SELECT subtotal_amount INTO expected_subtotal
      FROM sales_invoice_drafts
     WHERE tenant_id = invoice_tenant_id
       AND company_id = invoice_company_id
       AND id = invoice_id;

    -- If there is no parent (possible for an owner-driven cleanup), nothing remains to validate.
    IF NOT FOUND THEN
        RETURN NULL;
    END IF;

    SELECT count(*), COALESCE(sum(line_amount), 0)
      INTO actual_line_count, actual_subtotal
      FROM sales_invoice_draft_lines
     WHERE tenant_id = invoice_tenant_id
       AND company_id = invoice_company_id
       AND sales_invoice_draft_id = invoice_id;

    IF actual_line_count < 1 OR actual_subtotal <> expected_subtotal THEN
        RAISE EXCEPTION 'Sales invoice draft must contain lines and subtotal must equal the sum of line amounts'
            USING ERRCODE = '23514';
    END IF;

    RETURN NULL;
END $$;

CREATE CONSTRAINT TRIGGER trg_sales_invoice_draft_header_total
    AFTER INSERT OR UPDATE ON sales_invoice_drafts
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION assert_sales_invoice_draft_totals();

CREATE CONSTRAINT TRIGGER trg_sales_invoice_draft_lines_total
    AFTER INSERT OR UPDATE OR DELETE ON sales_invoice_draft_lines
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION assert_sales_invoice_draft_totals();

ALTER TABLE customers ENABLE ROW LEVEL SECURITY;
ALTER TABLE customers FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON customers
    USING (tenant_id = app_current_tenant_id())
    WITH CHECK (tenant_id = app_current_tenant_id());

ALTER TABLE sales_invoice_drafts ENABLE ROW LEVEL SECURITY;
ALTER TABLE sales_invoice_drafts FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON sales_invoice_drafts
    USING (tenant_id = app_current_tenant_id())
    WITH CHECK (tenant_id = app_current_tenant_id());

ALTER TABLE sales_invoice_draft_lines ENABLE ROW LEVEL SECURITY;
ALTER TABLE sales_invoice_draft_lines FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON sales_invoice_draft_lines
    USING (tenant_id = app_current_tenant_id())
    WITH CHECK (tenant_id = app_current_tenant_id());

COMMIT;
