-- CI only: create a non-owner, non-superuser app role so RLS is actually exercised.
-- Pass a disposable password with psql -v runtime_password=...; never use this role for migrations.
CREATE ROLE erp_ci_runtime
    LOGIN
    NOSUPERUSER
    NOCREATEDB
    NOCREATEROLE
    NOINHERIT
    NOBYPASSRLS
    PASSWORD :'runtime_password';

GRANT CONNECT ON DATABASE business_erp TO erp_ci_runtime;
GRANT USAGE ON SCHEMA public TO erp_ci_runtime;

GRANT SELECT ON users TO erp_ci_runtime;
GRANT SELECT ON subscription_plans, account_templates, account_template_accounts TO erp_ci_runtime;

GRANT SELECT, INSERT, UPDATE, DELETE ON
    tenants,
    tenant_memberships,
    tenant_subscriptions,
    companies,
    branches,
    fiscal_years,
    fiscal_periods,
    accounts,
    journal_entries,
    journal_entry_lines
TO erp_ci_runtime;

-- Audit is append-only for the application role.
GRANT SELECT, INSERT ON audit_logs TO erp_ci_runtime;
REVOKE UPDATE, DELETE, TRUNCATE ON audit_logs FROM erp_ci_runtime;


-- Sales master data is insert/read only. VAT calculation may update only the reviewed tax columns,
-- never invoice identity, customer, issue date, base subtotal, source lines, or status.
GRANT SELECT, INSERT ON customers, sales_invoice_drafts, sales_invoice_draft_lines TO erp_ci_runtime;
GRANT UPDATE (
    tax_calculation_status, gross_amount, discount_amount, net_amount, vat_amount, total_amount
) ON sales_invoice_drafts TO erp_ci_runtime;
GRANT UPDATE (
    discount_amount, vat_treatment, vat_treatment_reason, gross_amount, net_amount,
    vat_rate_percent, vat_amount, total_amount
) ON sales_invoice_draft_lines TO erp_ci_runtime;
