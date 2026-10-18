-- Run with the non-owner erp_ci_runtime login against the disposable CI database.
-- No tenant context must reveal no tenant-owned rows.
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM tenants) OR EXISTS (SELECT 1 FROM companies)
       OR EXISTS (SELECT 1 FROM customers)
       OR EXISTS (SELECT 1 FROM sales_invoice_drafts)
       OR EXISTS (SELECT 1 FROM sales_invoice_draft_lines) THEN
        RAISE EXCEPTION 'RLS smoke failed: tenant-owned rows visible without app.tenant_id';
    END IF;
END $$;

BEGIN;
SELECT set_config('app.tenant_id', '11111111-1111-4111-8111-111111111111', true);

DO $$
DECLARE
    visible_companies integer;
    posted_rows integer;
    blocked boolean := false;
    invalid_line_blocked boolean := false;
    unbalanced_blocked boolean := false;
BEGIN
    SELECT count(*) INTO visible_companies FROM companies;
    IF visible_companies <> 1 THEN
        RAISE EXCEPTION 'RLS smoke failed: expected one company in tenant A, found %', visible_companies;
    END IF;
    IF EXISTS (SELECT 1 FROM companies WHERE id = '22222222-2222-4222-8222-222222222201') THEN
        RAISE EXCEPTION 'RLS smoke failed: tenant A can see tenant B company';
    END IF;
    IF EXISTS (SELECT 1 FROM customers) OR EXISTS (SELECT 1 FROM sales_invoice_drafts)
       OR EXISTS (SELECT 1 FROM sales_invoice_draft_lines) THEN
        RAISE EXCEPTION 'Sales RLS smoke failed: tenant A unexpectedly sees unseeded sales rows';
    END IF;

    IF (SELECT count(*) FROM tenant_subscriptions) <> 1 OR NOT EXISTS (
        SELECT 1 FROM tenant_subscriptions s
        JOIN subscription_plans p ON p.id = s.subscription_plan_id
        WHERE s.tenant_id = '11111111-1111-4111-8111-111111111111'
          AND p.code = 'CI-STARTER' AND s.status = 'ACTIVE'
    ) THEN
        RAISE EXCEPTION 'Subscription smoke failed: active tenant subscription could not be read through RLS';
    END IF;

    UPDATE journal_entries SET status = 'APPROVED'
     WHERE id = '11111111-1111-4111-8111-111111111106';
    GET DIAGNOSTICS posted_rows = ROW_COUNT;
    IF posted_rows <> 1 THEN
        RAISE EXCEPTION 'Posting smoke failed: approved transition did not affect one row';
    END IF;

    UPDATE journal_entries SET status = 'POSTED'
     WHERE id = '11111111-1111-4111-8111-111111111106';
    GET DIAGNOSTICS posted_rows = ROW_COUNT;
    IF posted_rows <> 1 THEN
        RAISE EXCEPTION 'Posting smoke failed: posted transition did not affect one row';
    END IF;

    BEGIN
        UPDATE journal_entries SET description = 'tampered after posting'
         WHERE id = '11111111-1111-4111-8111-111111111106';
    EXCEPTION WHEN OTHERS THEN
        IF SQLERRM = 'Posted/cancelled journal entries are immutable; use a reversal/correction' THEN
            blocked := true;
        ELSE
            RAISE;
        END IF;
    END;
    IF NOT blocked THEN
        RAISE EXCEPTION 'Immutability smoke failed: posted journal header was editable';
    END IF;

    BEGIN
        UPDATE journal_entries SET status = 'APPROVED'
         WHERE id = '11111111-1111-4111-8111-111111111107';
    EXCEPTION WHEN OTHERS THEN
        IF SQLERRM = 'Journal entry must have at least two lines and equal debit/credit totals' THEN
            unbalanced_blocked := true;
        ELSE
            RAISE;
        END IF;
    END;
    IF NOT unbalanced_blocked THEN
        RAISE EXCEPTION 'Balance smoke failed: database approved an unbalanced entry';
    END IF;

    BEGIN
        INSERT INTO journal_entry_lines
            (tenant_id, company_id, journal_entry_id, account_id, line_number, debit, credit)
        VALUES
            ('11111111-1111-4111-8111-111111111111',
             '11111111-1111-4111-8111-111111111101',
             '11111111-1111-4111-8111-111111111108',
             '11111111-1111-4111-8111-111111111104', 1, 10, 10);
    EXCEPTION WHEN check_violation THEN
        invalid_line_blocked := true;
    END;
    IF NOT invalid_line_blocked THEN
        RAISE EXCEPTION 'Line smoke failed: line with both debit and credit was accepted';
    END IF;
END $$;

COMMIT;
