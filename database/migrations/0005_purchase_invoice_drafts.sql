-- Supplier master, purchase draft documents, and internal persisted VAT calculation.
-- This is not legal invoice issuance or proof that input VAT is recoverable.
BEGIN;

CREATE TABLE suppliers (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 tenant_id uuid NOT NULL, company_id uuid NOT NULL,
 supplier_code varchar(64) NOT NULL CHECK (length(btrim(supplier_code)) > 0 AND supplier_code = upper(supplier_code)),
 name varchar(200) NOT NULL CHECK (length(btrim(name)) > 0),
 email varchar(320), phone varchar(32), tax_number varchar(32),
 is_active boolean NOT NULL DEFAULT true, created_at timestamptz NOT NULL DEFAULT now(),
 UNIQUE (tenant_id, company_id, id), UNIQUE (tenant_id, company_id, supplier_code),
 FOREIGN KEY (tenant_id, company_id) REFERENCES companies(tenant_id, id) ON DELETE RESTRICT
);
CREATE INDEX ix_suppliers_tenant_company_name ON suppliers(tenant_id, company_id, name);

CREATE TABLE purchase_invoice_drafts (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 tenant_id uuid NOT NULL, company_id uuid NOT NULL, supplier_id uuid NOT NULL,
 purchase_number varchar(80) NOT NULL CHECK (length(btrim(purchase_number)) > 0 AND purchase_number = upper(purchase_number)),
 supplier_invoice_number varchar(80) NOT NULL CHECK (length(btrim(supplier_invoice_number)) > 0 AND supplier_invoice_number = upper(supplier_invoice_number)),
 issue_date date NOT NULL, due_date date NULL, currency_code char(3) NOT NULL,
 supplier_name_snapshot varchar(200) NOT NULL, supplier_tax_number_snapshot varchar(32),
 subtotal_amount numeric(20,4) NOT NULL CHECK (subtotal_amount >= 0),
 notes varchar(2000) NOT NULL DEFAULT '', status varchar(20) NOT NULL DEFAULT 'DRAFT' CHECK (status = 'DRAFT'),
 created_by uuid NOT NULL REFERENCES users(id) ON DELETE RESTRICT, created_at timestamptz NOT NULL DEFAULT now(),
 tax_calculation_status varchar(20) NOT NULL DEFAULT 'NOT_CALCULATED' CHECK (tax_calculation_status IN ('NOT_CALCULATED','CALCULATED')),
 gross_amount numeric(20,2), discount_amount numeric(20,2), net_amount numeric(20,2), vat_amount numeric(20,2), total_amount numeric(20,2),
 CHECK (due_date IS NULL OR due_date >= issue_date),
 CHECK (
   (tax_calculation_status = 'NOT_CALCULATED' AND gross_amount IS NULL AND discount_amount IS NULL AND net_amount IS NULL AND vat_amount IS NULL AND total_amount IS NULL)
   OR
   (tax_calculation_status = 'CALCULATED' AND gross_amount IS NOT NULL AND discount_amount IS NOT NULL AND net_amount IS NOT NULL AND vat_amount IS NOT NULL AND total_amount IS NOT NULL
    AND gross_amount >= 0 AND discount_amount >= 0 AND discount_amount <= gross_amount AND net_amount = gross_amount - discount_amount
    AND vat_amount >= 0 AND total_amount = net_amount + vat_amount)
 ),
 UNIQUE (tenant_id, company_id, id),
 UNIQUE (tenant_id, company_id, purchase_number),
 UNIQUE (tenant_id, company_id, supplier_id, supplier_invoice_number),
 FOREIGN KEY (tenant_id, company_id) REFERENCES companies(tenant_id, id) ON DELETE RESTRICT,
 FOREIGN KEY (tenant_id, company_id, supplier_id) REFERENCES suppliers(tenant_id, company_id, id) ON DELETE RESTRICT
);
CREATE INDEX ix_purchase_invoice_drafts_supplier_date ON purchase_invoice_drafts(tenant_id, company_id, supplier_id, issue_date DESC);
CREATE INDEX ix_purchase_invoice_drafts_date ON purchase_invoice_drafts(tenant_id, company_id, issue_date DESC);

CREATE TABLE purchase_invoice_draft_lines (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
 tenant_id uuid NOT NULL, company_id uuid NOT NULL, purchase_invoice_draft_id uuid NOT NULL,
 line_number integer NOT NULL CHECK (line_number > 0),
 description varchar(500) NOT NULL CHECK (length(btrim(description)) > 0),
 quantity numeric(20,4) NOT NULL CHECK (quantity > 0),
 unit_price numeric(20,4) NOT NULL CHECK (unit_price >= 0),
 line_amount numeric(20,4) NOT NULL CHECK (line_amount >= 0 AND line_amount = round(quantity * unit_price, 4)),
 discount_amount numeric(20,2) NOT NULL DEFAULT 0 CHECK (discount_amount >= 0),
 vat_treatment varchar(24) NOT NULL DEFAULT 'UNCLASSIFIED' CHECK (vat_treatment IN ('UNCLASSIFIED','StandardRated','ZeroRated','Exempt','OutsideScope')),
 vat_treatment_reason varchar(500), gross_amount numeric(20,2), net_amount numeric(20,2),
 vat_rate_percent numeric(5,2), vat_amount numeric(20,2), total_amount numeric(20,2),
 CHECK (
   (vat_treatment = 'UNCLASSIFIED' AND discount_amount = 0 AND vat_treatment_reason IS NULL AND gross_amount IS NULL AND net_amount IS NULL AND vat_rate_percent IS NULL AND vat_amount IS NULL AND total_amount IS NULL)
   OR
   (vat_treatment <> 'UNCLASSIFIED' AND gross_amount IS NOT NULL AND net_amount IS NOT NULL AND vat_rate_percent IS NOT NULL AND vat_amount IS NOT NULL AND total_amount IS NOT NULL
    AND gross_amount = round(quantity * unit_price, 2) AND discount_amount <= gross_amount AND net_amount = gross_amount - discount_amount
    AND ((vat_treatment = 'StandardRated' AND vat_rate_percent = 15)
      OR (vat_treatment IN ('ZeroRated','Exempt','OutsideScope') AND vat_rate_percent = 0 AND length(btrim(COALESCE(vat_treatment_reason,''))) > 0))
    AND vat_amount = round(net_amount * vat_rate_percent / 100, 2) AND total_amount = net_amount + vat_amount)
 ),
 UNIQUE (tenant_id, company_id, purchase_invoice_draft_id, line_number),
 FOREIGN KEY (tenant_id, company_id, purchase_invoice_draft_id) REFERENCES purchase_invoice_drafts(tenant_id, company_id, id) ON DELETE RESTRICT
);
CREATE INDEX ix_purchase_invoice_draft_lines_invoice ON purchase_invoice_draft_lines(tenant_id, company_id, purchase_invoice_draft_id, line_number);

CREATE OR REPLACE FUNCTION assert_purchase_invoice_draft_totals() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
 invoice_id uuid; invoice_tenant_id uuid; invoice_company_id uuid;
 expected_subtotal numeric(20,4); expected_tax_status varchar(20);
 expected_gross numeric(20,2); expected_discount numeric(20,2); expected_net numeric(20,2); expected_vat numeric(20,2); expected_total numeric(20,2);
 actual_subtotal numeric; actual_line_count integer; actual_tax_line_count integer;
 actual_gross numeric; actual_discount numeric; actual_net numeric; actual_vat numeric; actual_total numeric;
BEGIN
 IF TG_TABLE_NAME = 'purchase_invoice_drafts' THEN
  invoice_id := NEW.id; invoice_tenant_id := NEW.tenant_id; invoice_company_id := NEW.company_id;
 ELSIF TG_OP = 'DELETE' THEN
  invoice_id := OLD.purchase_invoice_draft_id; invoice_tenant_id := OLD.tenant_id; invoice_company_id := OLD.company_id;
 ELSE
  invoice_id := NEW.purchase_invoice_draft_id; invoice_tenant_id := NEW.tenant_id; invoice_company_id := NEW.company_id;
 END IF;
 SELECT subtotal_amount, tax_calculation_status, gross_amount, discount_amount, net_amount, vat_amount, total_amount
 INTO expected_subtotal, expected_tax_status, expected_gross, expected_discount, expected_net, expected_vat, expected_total
 FROM purchase_invoice_drafts WHERE tenant_id = invoice_tenant_id AND company_id = invoice_company_id AND id = invoice_id;
 IF NOT FOUND THEN RETURN NULL; END IF;
 SELECT count(*), COALESCE(sum(line_amount),0), count(*) FILTER (WHERE vat_treatment <> 'UNCLASSIFIED'),
        COALESCE(sum(gross_amount),0), COALESCE(sum(discount_amount),0), COALESCE(sum(net_amount),0),
        COALESCE(sum(vat_amount),0), COALESCE(sum(total_amount),0)
 INTO actual_line_count, actual_subtotal, actual_tax_line_count, actual_gross, actual_discount, actual_net, actual_vat, actual_total
 FROM purchase_invoice_draft_lines WHERE tenant_id = invoice_tenant_id AND company_id = invoice_company_id AND purchase_invoice_draft_id = invoice_id;
 IF actual_line_count < 1 OR actual_subtotal <> expected_subtotal THEN
  RAISE EXCEPTION 'Purchase draft must contain lines and subtotal must equal line sum' USING ERRCODE = '23514';
 END IF;
 IF expected_tax_status = 'NOT_CALCULATED' THEN
  IF actual_tax_line_count <> 0 THEN RAISE EXCEPTION 'Unclassified purchase draft cannot contain VAT-classified lines' USING ERRCODE = '23514'; END IF;
 ELSE
  IF actual_tax_line_count <> actual_line_count OR actual_gross <> expected_gross OR actual_discount <> expected_discount OR actual_net <> expected_net OR actual_vat <> expected_vat OR actual_total <> expected_total THEN
   RAISE EXCEPTION 'Purchase draft VAT header totals must match the line totals' USING ERRCODE = '23514';
  END IF;
 END IF;
 RETURN NULL;
END $$;

CREATE CONSTRAINT TRIGGER trg_purchase_draft_header_total AFTER INSERT OR UPDATE ON purchase_invoice_drafts
 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION assert_purchase_invoice_draft_totals();
CREATE CONSTRAINT TRIGGER trg_purchase_draft_lines_total AFTER INSERT OR UPDATE OR DELETE ON purchase_invoice_draft_lines
 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION assert_purchase_invoice_draft_totals();

ALTER TABLE suppliers ENABLE ROW LEVEL SECURITY;
ALTER TABLE suppliers FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON suppliers USING (tenant_id = app_current_tenant_id()) WITH CHECK (tenant_id = app_current_tenant_id());
ALTER TABLE purchase_invoice_drafts ENABLE ROW LEVEL SECURITY;
ALTER TABLE purchase_invoice_drafts FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON purchase_invoice_drafts USING (tenant_id = app_current_tenant_id()) WITH CHECK (tenant_id = app_current_tenant_id());
ALTER TABLE purchase_invoice_draft_lines ENABLE ROW LEVEL SECURITY;
ALTER TABLE purchase_invoice_draft_lines FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON purchase_invoice_draft_lines USING (tenant_id = app_current_tenant_id()) WITH CHECK (tenant_id = app_current_tenant_id());

COMMENT ON TABLE purchase_invoice_drafts IS 'Internal supplier purchase drafts, not legal invoice issuance or proof that input VAT is recoverable.';
COMMENT ON COLUMN purchase_invoice_drafts.vat_amount IS 'Internal calculation only; recoverability of input VAT must be reviewed against the supplier document and applicable rules.';
COMMIT;
