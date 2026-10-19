-- Run as the disposable PostgreSQL admin on a fresh CI database after 0001_initial.sql.
-- Fixed IDs make the smoke assertions explicit and repeatable.
INSERT INTO subscription_plans
    (id, code, name, billing_interval, price, currency, limits_json, is_active)
VALUES
    ('33333333-3333-4333-8333-333333333333', 'CI-STARTER', 'CI Starter', 'MONTHLY', 0.0000, 'SAR',
     '{"companies":2,"users":3,"entriesPerMonth":1000}'::jsonb, true),
    ('44444444-4444-4444-8444-444444444444', 'CI-NO-COMPANIES', 'CI No Companies', 'MONTHLY', 0.0000, 'SAR',
     '{"companies":0,"users":1,"entriesPerMonth":10}'::jsonb, true),
    ('55555555-5555-4555-8555-555555555555', 'CI-NO-ENTRIES', 'CI No Monthly Entries', 'MONTHLY', 0.0000, 'SAR',
     '{"companies":1,"users":1,"entriesPerMonth":0}'::jsonb, true);

INSERT INTO tenants (id, slug, name, status) VALUES
('11111111-1111-4111-8111-111111111111', 'ci-tenant-a', 'CI Tenant A', 'ACTIVE'),
('22222222-2222-4222-8222-222222222222', 'ci-tenant-b', 'CI Tenant B', 'ACTIVE'),
('33333333-3333-4333-8333-333333333301', 'ci-tenant-c', 'CI Tenant C', 'ACTIVE'),
('66666666-6666-4666-8666-666666666601', 'ci-tenant-d', 'CI Tenant D', 'ACTIVE');

INSERT INTO tenant_subscriptions
    (id, tenant_id, subscription_plan_id, status, starts_at, ends_at)
VALUES
    ('11111111-1111-4111-8111-111111111109', '11111111-1111-4111-8111-111111111111',
     '33333333-3333-4333-8333-333333333333', 'ACTIVE', '2026-01-01T00:00:00Z', '2027-01-01T00:00:00Z'),
    ('33333333-3333-4333-8333-333333333309', '33333333-3333-4333-8333-333333333301',
     '44444444-4444-4444-8444-444444444444', 'ACTIVE', '2026-01-01T00:00:00Z', '2027-01-01T00:00:00Z'),
    ('66666666-6666-4666-8666-666666666609', '66666666-6666-4666-8666-666666666601',
     '55555555-5555-4555-8555-555555555555', 'ACTIVE', '2026-01-01T00:00:00Z', '2027-01-01T00:00:00Z');

INSERT INTO users (id, oidc_subject, email, display_name) VALUES
('aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', 'ci-subject-a', 'ci-a@example.invalid', 'CI User A'),
('bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb', 'ci-subject-b', 'ci-b@example.invalid', 'CI User B'),
('cccccccc-cccc-4ccc-8ccc-ccccccccccc3', 'ci-subject-c', 'ci-c@example.invalid', 'CI User C'),
('dddddddd-dddd-4ddd-8ddd-ddddddddddd4', 'ci-subject-d', 'ci-d@example.invalid', 'CI User D');

INSERT INTO tenant_memberships (tenant_id, user_id, status, role_code) VALUES
('11111111-1111-4111-8111-111111111111', 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', 'ACTIVE', 'OWNER'),
('22222222-2222-4222-8222-222222222222', 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb', 'ACTIVE', 'OWNER'),
('33333333-3333-4333-8333-333333333301', 'cccccccc-cccc-4ccc-8ccc-ccccccccccc3', 'ACTIVE', 'OWNER'),
('66666666-6666-4666-8666-666666666601', 'dddddddd-dddd-4ddd-8ddd-ddddddddddd4', 'ACTIVE', 'OWNER');

INSERT INTO companies (id, tenant_id, name, base_currency) VALUES
('11111111-1111-4111-8111-111111111101', '11111111-1111-4111-8111-111111111111', 'CI Company A', 'SAR'),
('22222222-2222-4222-8222-222222222201', '22222222-2222-4222-8222-222222222222', 'CI Company B', 'SAR'),
('66666666-6666-4666-8666-666666666101', '66666666-6666-4666-8666-666666666601', 'CI Company D', 'SAR');

INSERT INTO fiscal_years (id, tenant_id, company_id, name, start_date, end_date, status) VALUES
('11111111-1111-4111-8111-111111111102', '11111111-1111-4111-8111-111111111111', '11111111-1111-4111-8111-111111111101', 'FY 2026 A', '2026-01-01', '2026-12-31', 'OPEN'),
('66666666-6666-4666-8666-666666666102', '66666666-6666-4666-8666-666666666601', '66666666-6666-4666-8666-666666666101', 'FY 2026 D', '2026-01-01', '2026-12-31', 'OPEN');

INSERT INTO fiscal_periods (id, tenant_id, company_id, fiscal_year_id, period_number, start_date, end_date, status) VALUES
('11111111-1111-4111-8111-111111111103', '11111111-1111-4111-8111-111111111111', '11111111-1111-4111-8111-111111111101', '11111111-1111-4111-8111-111111111102', 1, '2026-01-01', '2026-12-31', 'OPEN'),
('66666666-6666-4666-8666-666666666103', '66666666-6666-4666-8666-666666666601', '66666666-6666-4666-8666-666666666101', '66666666-6666-4666-8666-666666666102', 1, '2026-01-01', '2026-12-31', 'OPEN');

INSERT INTO accounts (id, tenant_id, company_id, code, name_ar, name_en, account_type, normal_balance, is_active, allow_posting) VALUES
('11111111-1111-4111-8111-111111111104', '11111111-1111-4111-8111-111111111111', '11111111-1111-4111-8111-111111111101', '1000', 'نقدية الاختبار', 'CI Cash', 'ASSET', 'DEBIT', true, true),
('11111111-1111-4111-8111-111111111105', '11111111-1111-4111-8111-111111111111', '11111111-1111-4111-8111-111111111101', '3000', 'حقوق الاختبار', 'CI Equity', 'EQUITY', 'CREDIT', true, true),
('11111111-1111-4111-8111-111111111110', '11111111-1111-4111-8111-111111111111', '11111111-1111-4111-8111-111111111101', '1200', 'مخزون الاختبار', 'CI Inventory Asset', 'ASSET', 'DEBIT', true, true),
('11111111-1111-4111-8111-111111111111', '11111111-1111-4111-8111-111111111111', '11111111-1111-4111-8111-111111111101', '5000', 'تكلفة البضاعة المباعة', 'CI Cost of Goods Sold', 'EXPENSE', 'DEBIT', true, true),
('66666666-6666-4666-8666-666666666104', '66666666-6666-4666-8666-666666666601', '66666666-6666-4666-8666-666666666101', '1000', 'نقدية الاشتراك', 'Quota test cash', 'ASSET', 'DEBIT', true, true),
('66666666-6666-4666-8666-666666666105', '66666666-6666-4666-8666-666666666601', '66666666-6666-4666-8666-666666666101', '3000', 'حقوق الاشتراك', 'Quota test equity', 'EQUITY', 'CREDIT', true, true);

INSERT INTO journal_entries (id, tenant_id, company_id, fiscal_period_id, entry_number, entry_date, description, currency_code, status) VALUES
('11111111-1111-4111-8111-111111111106', '11111111-1111-4111-8111-111111111111', '11111111-1111-4111-8111-111111111101', '11111111-1111-4111-8111-111111111103', 'CI-BALANCED-1', '2026-01-15', 'Balanced smoke entry', 'SAR', 'DRAFT'),
('11111111-1111-4111-8111-111111111107', '11111111-1111-4111-8111-111111111111', '11111111-1111-4111-8111-111111111101', '11111111-1111-4111-8111-111111111103', 'CI-UNBALANCED-1', '2026-01-15', 'Unbalanced smoke entry', 'SAR', 'DRAFT'),
('11111111-1111-4111-8111-111111111108', '11111111-1111-4111-8111-111111111111', '11111111-1111-4111-8111-111111111101', '11111111-1111-4111-8111-111111111103', 'CI-LINE-CONSTRAINT-1', '2026-01-15', 'Line constraint smoke entry', 'SAR', 'DRAFT');

INSERT INTO journal_entry_lines (tenant_id, company_id, journal_entry_id, account_id, line_number, debit, credit) VALUES
('11111111-1111-4111-8111-111111111111', '11111111-1111-4111-8111-111111111101', '11111111-1111-4111-8111-111111111106', '11111111-1111-4111-8111-111111111104', 1, 100.0000, 0),
('11111111-1111-4111-8111-111111111111', '11111111-1111-4111-8111-111111111101', '11111111-1111-4111-8111-111111111106', '11111111-1111-4111-8111-111111111105', 2, 0, 100.0000),
('11111111-1111-4111-8111-111111111111', '11111111-1111-4111-8111-111111111101', '11111111-1111-4111-8111-111111111107', '11111111-1111-4111-8111-111111111104', 1, 25.0000, 0);


-- Sales RLS fixtures deliberately include customer rows for both tenants.
INSERT INTO customers (id, tenant_id, company_id, customer_code, name, tax_number) VALUES
('cccccccc-cccc-4ccc-8ccc-ccccccccccc1', '11111111-1111-4111-8111-111111111111',
 '11111111-1111-4111-8111-111111111101', 'CI-CUSTOMER-A', 'Tenant A customer', '300000000000001'),
('cccccccc-cccc-4ccc-8ccc-ccccccccccc2', '22222222-2222-4222-8222-222222222222',
 '22222222-2222-4222-8222-222222222201', 'CI-CUSTOMER-B', 'Tenant B customer', '300000000000002');


-- Seed a real tenant B invoice. Header and lines commit together because of deferred total checks.
BEGIN;
INSERT INTO sales_invoice_drafts
    (id, tenant_id, company_id, customer_id, invoice_number, issue_date, currency_code,
     customer_name_snapshot, customer_tax_number_snapshot, subtotal_amount, notes, status, created_by)
VALUES
    ('22222222-2222-4222-8222-222222222203',
     '22222222-2222-4222-8222-222222222222',
     '22222222-2222-4222-8222-222222222201',
     'cccccccc-cccc-4ccc-8ccc-ccccccccccc2',
     'B-INVOICE-1', '2026-01-10', 'SAR', 'Tenant B customer',
     '300000000000002', 100.0000, 'RLS fixture only', 'DRAFT',
     'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb');
INSERT INTO sales_invoice_draft_lines
    (id, tenant_id, company_id, sales_invoice_draft_id, line_number, description, quantity, unit_price, line_amount)
VALUES
    ('22222222-2222-4222-8222-222222222204',
     '22222222-2222-4222-8222-222222222222',
     '22222222-2222-4222-8222-222222222201',
     '22222222-2222-4222-8222-222222222203',
     1, 'Tenant B private service', 1.0000, 100.0000, 100.0000);
COMMIT;
