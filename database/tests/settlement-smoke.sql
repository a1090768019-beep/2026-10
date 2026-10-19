-- Run with the non-owner erp_ci_runtime login after migrations and postgres-fixture.sql.
-- Exercises tenant visibility and ensures settlement allocations cannot target unposted invoices.
BEGIN;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM customer_receipts)
       OR EXISTS (SELECT 1 FROM customer_receipt_allocations)
       OR EXISTS (SELECT 1 FROM supplier_payments)
       OR EXISTS (SELECT 1 FROM supplier_payment_allocations) THEN
        RAISE EXCEPTION 'Settlement RLS smoke failed: rows visible without tenant context';
    END IF;
END $$;

SELECT set_config('app.tenant_id', '22222222-2222-4222-8222-222222222222', true);

INSERT INTO suppliers (id, tenant_id, company_id, supplier_code, name, is_active)
VALUES ('eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee1',
        '22222222-2222-4222-8222-222222222222',
        '22222222-2222-4222-8222-222222222201',
        'CI-SETTLEMENT-SUP-B', 'CI Settlement Supplier B', true);

INSERT INTO purchase_invoice_drafts
    (id, tenant_id, company_id, supplier_id, purchase_number, supplier_invoice_number, issue_date,
     due_date, currency_code, supplier_name_snapshot, subtotal_amount, notes, status, created_by)
VALUES
    ('eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee2',
     '22222222-2222-4222-8222-222222222222',
     '22222222-2222-4222-8222-222222222201',
     'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee1',
     'CI-SETTLEMENT-PUR-B', 'CI-SETTLEMENT-SUP-INV-B', '2026-01-15', '2026-02-15',
     'SAR', 'CI Settlement Supplier B', 100.0000, 'settlement smoke', 'DRAFT',
     'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb');

INSERT INTO purchase_invoice_draft_lines
    (id, tenant_id, company_id, purchase_invoice_draft_id, line_number, description, quantity, unit_price, line_amount)
VALUES
    ('eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee3',
     '22222222-2222-4222-8222-222222222222',
     '22222222-2222-4222-8222-222222222201',
     'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee2', 1, 'Settlement smoke item', 1.0000, 100.0000, 100.0000);

INSERT INTO customer_receipts
    (id, tenant_id, company_id, customer_id, receipt_number, receipt_date, currency_code, amount, notes, created_by)
VALUES
    ('eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee4',
     '22222222-2222-4222-8222-222222222222',
     '22222222-2222-4222-8222-222222222201',
     'cccccccc-cccc-4ccc-8ccc-ccccccccccc2',
     'CI-SETTLEMENT-RCP-B', '2026-01-15', 'SAR', 100.00, 'settlement smoke',
     'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb');

INSERT INTO supplier_payments
    (id, tenant_id, company_id, supplier_id, payment_number, payment_date, currency_code, amount, notes, created_by)
VALUES
    ('eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee5',
     '22222222-2222-4222-8222-222222222222',
     '22222222-2222-4222-8222-222222222201',
     'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee1',
     'CI-SETTLEMENT-PAY-B', '2026-01-15', 'SAR', 100.00, 'settlement smoke',
     'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb');

SELECT set_config('app.tenant_id', '11111111-1111-4111-8111-111111111111', true);
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM customer_receipts)
       OR EXISTS (SELECT 1 FROM customer_receipt_allocations)
       OR EXISTS (SELECT 1 FROM supplier_payments)
       OR EXISTS (SELECT 1 FROM supplier_payment_allocations) THEN
        RAISE EXCEPTION 'Settlement RLS smoke failed: tenant A can see tenant B settlement records';
    END IF;
END $$;

SELECT set_config('app.tenant_id', '22222222-2222-4222-8222-222222222222', true);
DO $$
DECLARE
    receipt_draft_invoice_rejected boolean := false;
    payment_draft_invoice_rejected boolean := false;
    receipt_without_journal_rejected boolean := false;
BEGIN
    BEGIN
        INSERT INTO customer_receipt_allocations
            (id, tenant_id, company_id, receipt_id, sales_invoice_draft_id, allocated_amount)
        VALUES
            ('eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee6',
             '22222222-2222-4222-8222-222222222222',
             '22222222-2222-4222-8222-222222222201',
             'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee4',
             '22222222-2222-4222-8222-222222222203', 100.00);
    EXCEPTION WHEN check_violation THEN
        receipt_draft_invoice_rejected := true;
    END;
    IF NOT receipt_draft_invoice_rejected THEN
        RAISE EXCEPTION 'Settlement smoke failed: receipt allocation to a DRAFT sales invoice was accepted';
    END IF;

    BEGIN
        INSERT INTO supplier_payment_allocations
            (id, tenant_id, company_id, payment_id, purchase_invoice_draft_id, allocated_amount)
        VALUES
            ('eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee7',
             '22222222-2222-4222-8222-222222222222',
             '22222222-2222-4222-8222-222222222201',
             'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee5',
             'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee2', 100.00);
    EXCEPTION WHEN check_violation THEN
        payment_draft_invoice_rejected := true;
    END;
    IF NOT payment_draft_invoice_rejected THEN
        RAISE EXCEPTION 'Settlement smoke failed: payment allocation to a DRAFT purchase invoice was accepted';
    END IF;

    BEGIN
        UPDATE customer_receipts
           SET status = 'ACCOUNTING_POSTED', accounting_posted_at = now()
         WHERE id = 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeee4';
    EXCEPTION WHEN check_violation THEN
        receipt_without_journal_rejected := true;
    END;
    IF NOT receipt_without_journal_rejected THEN
        RAISE EXCEPTION 'Settlement smoke failed: receipt posted without a linked journal';
    END IF;
END $$;

ROLLBACK;
