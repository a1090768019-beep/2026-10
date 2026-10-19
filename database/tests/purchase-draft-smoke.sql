-- Run as erp_ci_runtime after the complete migration set and fixture seed.
-- Exercise actual RLS, supplier/purchase inserts, deferred totals, selective UPDATE grants,
-- and tenant separation for purchase VAT data.
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM suppliers)
       OR EXISTS (SELECT 1 FROM purchase_invoice_drafts)
       OR EXISTS (SELECT 1 FROM purchase_invoice_draft_lines) THEN
        RAISE EXCEPTION 'Purchase RLS smoke failed: data visible without an app tenant context';
    END IF;
END $$;

BEGIN;
SELECT set_config('app.tenant_id', '11111111-1111-4111-8111-111111111111', true);

INSERT INTO suppliers (id, tenant_id, company_id, supplier_code, name, tax_number)
VALUES ('eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee1',
        '11111111-1111-4111-8111-111111111111',
        '11111111-1111-4111-8111-111111111101',
        'CI-SUPPLIER-A', 'Tenant A CI supplier', '300000000000010');

INSERT INTO purchase_invoice_drafts
    (id, tenant_id, company_id, supplier_id, purchase_number, supplier_invoice_number,
     issue_date, due_date, currency_code, supplier_name_snapshot, supplier_tax_number_snapshot,
     subtotal_amount, notes, status, created_by)
VALUES ('eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee2',
        '11111111-1111-4111-8111-111111111111',
        '11111111-1111-4111-8111-111111111101',
        'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee1',
        'PUR-CI-0001', 'SUP-CI-0001', '2026-04-01', '2026-04-30',
        'SAR', 'Tenant A CI supplier', '300000000000010',
        100.0000, 'CI purchase draft', 'DRAFT',
        'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa');

INSERT INTO purchase_invoice_draft_lines
    (id, tenant_id, company_id, purchase_invoice_draft_id, line_number,
     description, quantity, unit_price, line_amount)
VALUES ('eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee3',
        '11111111-1111-4111-8111-111111111111',
        '11111111-1111-4111-8111-111111111101',
        'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee2',
        1, 'CI monitor', 1.0000, 100.0000, 100.0000);
COMMIT;

-- Use only the explicit tax-column grants; the base invoice identity and subtotal remain immutable to VAT workflow.
BEGIN;
SELECT set_config('app.tenant_id', '11111111-1111-4111-8111-111111111111', true);

UPDATE purchase_invoice_draft_lines
   SET discount_amount = 0.00,
       vat_treatment = 'StandardRated',
       vat_treatment_reason = NULL,
       gross_amount = 100.00,
       net_amount = 100.00,
       vat_rate_percent = 15.00,
       vat_amount = 15.00,
       total_amount = 115.00
 WHERE id = 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee3';

UPDATE purchase_invoice_drafts
   SET tax_calculation_status = 'CALCULATED',
       gross_amount = 100.00,
       discount_amount = 0.00,
       net_amount = 100.00,
       vat_amount = 15.00,
       total_amount = 115.00
 WHERE id = 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee2';
COMMIT;

BEGIN;
SELECT set_config('app.tenant_id', '11111111-1111-4111-8111-111111111111', true);
DO $$
BEGIN
    IF (SELECT count(*) FROM suppliers WHERE supplier_code = 'CI-SUPPLIER-A') <> 1
       OR (SELECT count(*) FROM purchase_invoice_drafts WHERE purchase_number = 'PUR-CI-0001') <> 1
       OR (SELECT count(*) FROM purchase_invoice_draft_lines WHERE purchase_invoice_draft_id = 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee2') <> 1
       OR NOT EXISTS (
            SELECT 1 FROM purchase_invoice_drafts
             WHERE purchase_number = 'PUR-CI-0001'
               AND tax_calculation_status = 'CALCULATED'
               AND gross_amount = 100 AND net_amount = 100 AND vat_amount = 15 AND total_amount = 115
       ) THEN
        RAISE EXCEPTION 'Purchase VAT smoke failed: tenant A could not read its saved draft/VAT totals';
    END IF;
END $$;

SELECT set_config('app.tenant_id', '22222222-2222-4222-8222-222222222222', true);
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM suppliers WHERE supplier_code = 'CI-SUPPLIER-A')
       OR EXISTS (SELECT 1 FROM purchase_invoice_drafts WHERE purchase_number = 'PUR-CI-0001')
       OR EXISTS (SELECT 1 FROM purchase_invoice_draft_lines WHERE purchase_invoice_draft_id = 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee2') THEN
        RAISE EXCEPTION 'Purchase RLS smoke failed: tenant B can view tenant A purchase data';
    END IF;
END $$;
COMMIT;
