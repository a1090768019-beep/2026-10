-- Run as the disposable PostgreSQL admin on a fresh CI database after 0001_initial.sql.
-- Fixed IDs make the smoke assertions explicit and repeatable.
INSERT INTO subscription_plans
    (id, code, name, billing_interval, price, currency, limits_json, is_active)
VALUES
    ('33333333-3333-4333-8333-333333333333', 'CI-STARTER', 'CI Starter', 'MONTHLY', 0.0000, 'SAR',
     '{"companies":1,"users":3,"entriesPerMonth":1000}'::jsonb, true);

INSERT INTO tenants (id, slug, name, status) VALUES
('11111111-1111-4111-8111-111111111111', 'ci-tenant-a', 'CI Tenant A', 'ACTIVE'),
('22222222-2222-4222-8222-222222222222', 'ci-tenant-b', 'CI Tenant B', 'ACTIVE');

INSERT INTO tenant_subscriptions
    (id, tenant_id, subscription_plan_id, status, starts_at, ends_at)
VALUES
    ('11111111-1111-4111-8111-111111111109', '11111111-1111-4111-8111-111111111111',
     '33333333-3333-4333-8333-333333333333', 'ACTIVE', '2026-01-01T00:00:00Z', '2027-01-01T00:00:00Z');

INSERT INTO users (id, oidc_subject, email, display_name) VALUES
('aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', 'ci-subject-a', 'ci-a@example.invalid', 'CI User A'),
('bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb', 'ci-subject-b', 'ci-b@example.invalid', 'CI User B');

INSERT INTO tenant_memberships (tenant_id, user_id, status, role_code) VALUES
('11111111-1111-4111-8111-111111111111', 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', 'ACTIVE', 'OWNER'),
('22222222-2222-4222-8222-222222222222', 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb', 'ACTIVE', 'OWNER');

INSERT INTO companies (id, tenant_id, name, base_currency) VALUES
('11111111-1111-4111-8111-111111111101', '11111111-1111-4111-8111-111111111111', 'CI Company A', 'SAR'),
('22222222-2222-4222-8222-222222222201', '22222222-2222-4222-8222-222222222222', 'CI Company B', 'SAR');

INSERT INTO fiscal_years (id, tenant_id, company_id, name, start_date, end_date, status) VALUES
('11111111-1111-4111-8111-111111111102', '11111111-1111-4111-8111-111111111111', '11111111-1111-4111-8111-111111111101', 'FY 2026 A', '2026-01-01', '2026-12-31', 'OPEN');

INSERT INTO fiscal_periods (id, tenant_id, company_id, fiscal_year_id, period_number, start_date, end_date, status) VALUES
('11111111-1111-4111-8111-111111111103', '11111111-1111-4111-8111-111111111111', '11111111-1111-4111-8111-111111111101', '11111111-1111-4111-8111-111111111102', 1, '2026-01-01', '2026-12-31', 'OPEN');

INSERT INTO accounts (id, tenant_id, company_id, code, name_ar, name_en, account_type, normal_balance, is_active, allow_posting) VALUES
('11111111-1111-4111-8111-111111111104', '11111111-1111-4111-8111-111111111111', '11111111-1111-4111-8111-111111111101', '1000', 'نقدية الاختبار', 'CI Cash', 'ASSET', 'DEBIT', true, true),
('11111111-1111-4111-8111-111111111105', '11111111-1111-4111-8111-111111111111', '11111111-1111-4111-8111-111111111101', '3000', 'حقوق الاختبار', 'CI Equity', 'EQUITY', 'CREDIT', true, true);

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
