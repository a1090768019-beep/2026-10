-- Persist explicit VAT calculation results on sales drafts; this does not issue a tax invoice.
BEGIN;

ALTER TABLE sales_invoice_drafts
    ADD COLUMN tax_calculation_status varchar(20) NOT NULL DEFAULT 'NOT_CALCULATED'
        CHECK (tax_calculation_status IN ('NOT_CALCULATED', 'CALCULATED')),
    ADD COLUMN gross_amount numeric(20,2),
    ADD COLUMN discount_amount numeric(20,2),
    ADD COLUMN net_amount numeric(20,2),
    ADD COLUMN vat_amount numeric(20,2),
    ADD COLUMN total_amount numeric(20,2),
    ADD CONSTRAINT ck_sales_invoice_draft_tax_totals CHECK (
        (tax_calculation_status = 'NOT_CALCULATED'
         AND gross_amount IS NULL AND discount_amount IS NULL AND net_amount IS NULL
         AND vat_amount IS NULL AND total_amount IS NULL)
        OR
        (tax_calculation_status = 'CALCULATED'
         AND gross_amount IS NOT NULL AND discount_amount IS NOT NULL AND net_amount IS NOT NULL
         AND vat_amount IS NOT NULL AND total_amount IS NOT NULL
         AND gross_amount >= 0 AND discount_amount >= 0 AND discount_amount <= gross_amount
         AND net_amount = gross_amount - discount_amount
         AND vat_amount >= 0 AND total_amount = net_amount + vat_amount)
    );

ALTER TABLE sales_invoice_draft_lines
    ADD COLUMN discount_amount numeric(20,2) NOT NULL DEFAULT 0 CHECK (discount_amount >= 0),
    ADD COLUMN vat_treatment varchar(24) NOT NULL DEFAULT 'UNCLASSIFIED'
        CHECK (vat_treatment IN ('UNCLASSIFIED', 'StandardRated', 'ZeroRated', 'Exempt', 'OutsideScope')),
    ADD COLUMN vat_treatment_reason varchar(500),
    ADD COLUMN gross_amount numeric(20,2),
    ADD COLUMN net_amount numeric(20,2),
    ADD COLUMN vat_rate_percent numeric(5,2),
    ADD COLUMN vat_amount numeric(20,2),
    ADD COLUMN total_amount numeric(20,2),
    ADD CONSTRAINT ck_sales_invoice_draft_line_tax CHECK (
        (vat_treatment = 'UNCLASSIFIED'
         AND discount_amount = 0 AND vat_treatment_reason IS NULL
         AND gross_amount IS NULL AND net_amount IS NULL AND vat_rate_percent IS NULL
         AND vat_amount IS NULL AND total_amount IS NULL)
        OR
        (vat_treatment <> 'UNCLASSIFIED'
         AND gross_amount IS NOT NULL AND net_amount IS NOT NULL AND vat_rate_percent IS NOT NULL
         AND vat_amount IS NOT NULL AND total_amount IS NOT NULL
         AND gross_amount = round(quantity * unit_price, 2)
         AND discount_amount <= gross_amount
         AND net_amount = gross_amount - discount_amount
         AND ((vat_treatment = 'StandardRated' AND vat_rate_percent = 15)
              OR (vat_treatment IN ('ZeroRated', 'Exempt', 'OutsideScope') AND vat_rate_percent = 0
                  AND length(btrim(COALESCE(vat_treatment_reason, ''))) > 0))
         AND vat_amount = round(net_amount * vat_rate_percent / 100, 2)
         AND total_amount = net_amount + vat_amount)
    );

CREATE OR REPLACE FUNCTION assert_sales_invoice_draft_totals() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    invoice_id uuid;
    invoice_tenant_id uuid;
    invoice_company_id uuid;
    expected_subtotal numeric(20,4);
    expected_tax_status varchar(20);
    expected_gross numeric(20,2);
    expected_discount numeric(20,2);
    expected_net numeric(20,2);
    expected_vat numeric(20,2);
    expected_total numeric(20,2);
    actual_subtotal numeric;
    actual_line_count integer;
    actual_tax_line_count integer;
    actual_gross numeric;
    actual_discount numeric;
    actual_net numeric;
    actual_vat numeric;
    actual_total numeric;
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

    SELECT subtotal_amount, tax_calculation_status, gross_amount, discount_amount,
           net_amount, vat_amount, total_amount
      INTO expected_subtotal, expected_tax_status, expected_gross, expected_discount,
           expected_net, expected_vat, expected_total
      FROM sales_invoice_drafts
     WHERE tenant_id = invoice_tenant_id AND company_id = invoice_company_id AND id = invoice_id;
    IF NOT FOUND THEN RETURN NULL; END IF;

    SELECT count(*), COALESCE(sum(line_amount), 0),
           count(*) FILTER (WHERE vat_treatment <> 'UNCLASSIFIED'),
           COALESCE(sum(gross_amount), 0), COALESCE(sum(discount_amount), 0),
           COALESCE(sum(net_amount), 0), COALESCE(sum(vat_amount), 0), COALESCE(sum(total_amount), 0)
      INTO actual_line_count, actual_subtotal, actual_tax_line_count, actual_gross,
           actual_discount, actual_net, actual_vat, actual_total
      FROM sales_invoice_draft_lines
     WHERE tenant_id = invoice_tenant_id AND company_id = invoice_company_id
       AND sales_invoice_draft_id = invoice_id;

    IF actual_line_count < 1 OR actual_subtotal <> expected_subtotal THEN
        RAISE EXCEPTION 'Sales invoice draft must contain lines and subtotal must equal the sum of line amounts'
            USING ERRCODE = '23514';
    END IF;
    IF expected_tax_status = 'NOT_CALCULATED' THEN
        IF actual_tax_line_count <> 0 THEN
            RAISE EXCEPTION 'Unclassified sales invoice draft cannot contain VAT-classified lines'
                USING ERRCODE = '23514';
        END IF;
    ELSE
        IF actual_tax_line_count <> actual_line_count OR actual_gross <> expected_gross
           OR actual_discount <> expected_discount OR actual_net <> expected_net
           OR actual_vat <> expected_vat OR actual_total <> expected_total THEN
            RAISE EXCEPTION 'Sales invoice draft VAT header totals must equal the sum of calculated line amounts'
                USING ERRCODE = '23514';
        END IF;
    END IF;
    RETURN NULL;
END $$;

COMMENT ON COLUMN sales_invoice_drafts.tax_calculation_status IS
    'NOT_CALCULATED for legacy/unclassified drafts; CALCULATED means an internal calculation only, not a legally issued or ZATCA-cleared invoice.';
COMMIT;
