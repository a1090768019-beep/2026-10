-- Business ERP bootstrap schema, PostgreSQL.
-- Apply with a schema/migration owner. The runtime application role must not own these tables,
-- must not be SUPERUSER, and must not have BYPASSRLS.
BEGIN;

CREATE TABLE tenants (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    slug varchar(80) NOT NULL UNIQUE,
    name varchar(200) NOT NULL,
    status varchar(24) NOT NULL DEFAULT 'ACTIVE' CHECK (status IN ('ACTIVE','SUSPENDED','CLOSED')),
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE users (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    oidc_subject varchar(300) NOT NULL UNIQUE,
    email varchar(320) NOT NULL,
    display_name varchar(200) NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    disabled_at timestamptz NULL
);

CREATE TABLE tenant_memberships (
    tenant_id uuid NOT NULL REFERENCES tenants(id) ON DELETE RESTRICT,
    user_id uuid NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
    status varchar(24) NOT NULL DEFAULT 'ACTIVE' CHECK (status IN ('INVITED','ACTIVE','SUSPENDED','REMOVED')),
    role_code varchar(24) NOT NULL DEFAULT 'ACCOUNTANT' CHECK (role_code IN ('OWNER','ADMIN','ACCOUNTANT','VIEWER')),
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, user_id)
);
CREATE INDEX ix_tenant_memberships_user ON tenant_memberships(user_id, status);

CREATE TABLE subscription_plans (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    code varchar(64) NOT NULL UNIQUE,
    name varchar(120) NOT NULL,
    billing_interval varchar(12) NOT NULL CHECK (billing_interval IN ('MONTHLY','YEARLY')),
    price numeric(20,4) NOT NULL CHECK (price >= 0),
    currency char(3) NOT NULL DEFAULT 'SAR',
    limits_json jsonb NOT NULL DEFAULT '{}'::jsonb,
    is_active boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE tenant_subscriptions (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL REFERENCES tenants(id) ON DELETE RESTRICT,
    subscription_plan_id uuid NOT NULL REFERENCES subscription_plans(id) ON DELETE RESTRICT,
    status varchar(24) NOT NULL CHECK (status IN ('TRIALING','ACTIVE','PAST_DUE','SUSPENDED','CANCELLED','EXPIRED')),
    starts_at timestamptz NOT NULL,
    ends_at timestamptz NULL,
    external_customer_id varchar(200) NULL,
    external_subscription_id varchar(200) NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, id)
);
CREATE INDEX ix_tenant_subscriptions_tenant_status ON tenant_subscriptions(tenant_id, status);

-- Global, platform-maintained chart templates. They are reference data, not tenant-owned.
-- Runtime application credentials should have SELECT permission here; template maintenance should
-- be performed through a controlled deployment/admin process, never by customer-facing requests.
CREATE TABLE account_templates (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    code varchar(64) NOT NULL UNIQUE,
    industry_code varchar(64) NOT NULL UNIQUE,
    name_ar varchar(200) NOT NULL,
    name_en varchar(200) NOT NULL,
    description_ar varchar(1000) NOT NULL DEFAULT '',
    is_active boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE account_template_accounts (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    template_id uuid NOT NULL REFERENCES account_templates(id) ON DELETE RESTRICT,
    code varchar(64) NOT NULL,
    parent_code varchar(64) NULL,
    name_ar varchar(200) NOT NULL,
    name_en varchar(200) NOT NULL,
    account_type varchar(24) NOT NULL CHECK (account_type IN ('ASSET','LIABILITY','EQUITY','REVENUE','EXPENSE')),
    normal_balance varchar(6) NOT NULL CHECK (normal_balance IN ('DEBIT','CREDIT')),
    allow_posting boolean NOT NULL DEFAULT true,
    sort_order integer NOT NULL DEFAULT 0,
    UNIQUE (template_id, code),
    CHECK (parent_code IS NULL OR parent_code <> code)
);
CREATE INDEX ix_account_template_accounts_order ON account_template_accounts(template_id, sort_order, code);

INSERT INTO account_templates (code, industry_code, name_ar, name_en, description_ar) VALUES
('TRADE', 'TRADE', 'التجارة', 'Trading', 'قالب أولي للمنشآت التجارية؛ راجع الحسابات وسياسات المخزون والضرائب قبل الاستخدام.'),
('SERVICES', 'SERVICES', 'الخدمات', 'Services', 'قالب أولي للمنشآت الخدمية؛ عدّل الإيرادات والتكاليف بحسب نشاطك.'),
('CONTRACTING', 'CONTRACTING', 'المقاولات', 'Contracting', 'قالب أولي للمقاولات؛ لا يحسب تكاليف المشاريع أو نسب الإنجاز تلقائيًا.'),
('RESTAURANTS', 'RESTAURANTS', 'المطاعم', 'Restaurants', 'قالب أولي للمطاعم؛ لا يتضمن بعد تكامل نقاط البيع أو وصفات التصنيع.'),
('MANUFACTURING', 'MANUFACTURING', 'التصنيع', 'Manufacturing', 'قالب أولي للتصنيع؛ لا ينفذ احتساب تكلفة الإنتاج أو WIP آليًا.'),
('REAL_ESTATE', 'REAL_ESTATE', 'العقارات', 'Real Estate', 'قالب أولي للعقارات؛ لا يعالج محاسبة العقود أو الاعتراف بالإيراد تلقائيًا.');

WITH common_accounts(code, parent_code, name_ar, name_en, account_type, normal_balance, allow_posting, sort_order) AS (
    VALUES
    ('1000', NULL, 'الأصول', 'Assets', 'ASSET', 'DEBIT', false, 10),
    ('1100', '1000', 'الأصول المتداولة', 'Current assets', 'ASSET', 'DEBIT', false, 20),
    ('1110', '1100', 'الصندوق', 'Cash on hand', 'ASSET', 'DEBIT', true, 30),
    ('1120', '1100', 'البنوك', 'Bank accounts', 'ASSET', 'DEBIT', true, 40),
    ('1130', '1100', 'العملاء', 'Trade receivables', 'ASSET', 'DEBIT', true, 50),
    ('1140', '1100', 'المخزون', 'Inventory', 'ASSET', 'DEBIT', true, 60),
    ('1150', '1100', 'ضريبة القيمة المضافة - مدخلات', 'VAT input', 'ASSET', 'DEBIT', true, 70),
    ('1200', '1000', 'الأصول غير المتداولة', 'Non-current assets', 'ASSET', 'DEBIT', false, 80),
    ('1210', '1200', 'الأثاث والمعدات', 'Furniture and equipment', 'ASSET', 'DEBIT', true, 90),
    ('1220', '1200', 'مجمع الإهلاك', 'Accumulated depreciation', 'ASSET', 'CREDIT', true, 100),
    ('2000', NULL, 'الالتزامات', 'Liabilities', 'LIABILITY', 'CREDIT', false, 110),
    ('2100', '2000', 'الالتزامات المتداولة', 'Current liabilities', 'LIABILITY', 'CREDIT', false, 120),
    ('2110', '2100', 'الموردون', 'Trade payables', 'LIABILITY', 'CREDIT', true, 130),
    ('2120', '2100', 'ضريبة القيمة المضافة - مخرجات', 'VAT output', 'LIABILITY', 'CREDIT', true, 140),
    ('2130', '2100', 'مصروفات مستحقة', 'Accrued expenses', 'LIABILITY', 'CREDIT', true, 150),
    ('3000', NULL, 'حقوق الملكية', 'Equity', 'EQUITY', 'CREDIT', false, 160),
    ('3100', '3000', 'رأس المال', 'Capital', 'EQUITY', 'CREDIT', true, 170),
    ('3200', '3000', 'الأرباح المبقاة', 'Retained earnings', 'EQUITY', 'CREDIT', true, 180),
    ('4000', NULL, 'الإيرادات', 'Revenue', 'REVENUE', 'CREDIT', false, 190),
    ('4100', '4000', 'الإيرادات التشغيلية', 'Operating revenue', 'REVENUE', 'CREDIT', true, 200),
    ('4200', '4000', 'مردودات وخصومات الإيرادات', 'Sales returns and discounts', 'REVENUE', 'DEBIT', true, 210),
    ('5000', NULL, 'التكاليف والمصروفات', 'Costs and expenses', 'EXPENSE', 'DEBIT', false, 220),
    ('5100', '5000', 'تكلفة المبيعات أو تقديم الخدمة', 'Cost of sales or service delivery', 'EXPENSE', 'DEBIT', true, 230),
    ('5200', '5000', 'الرواتب والأجور', 'Salaries and wages', 'EXPENSE', 'DEBIT', true, 240),
    ('5300', '5000', 'الإيجار', 'Rent', 'EXPENSE', 'DEBIT', true, 250),
    ('5400', '5000', 'الخدمات والمرافق', 'Utilities', 'EXPENSE', 'DEBIT', true, 260),
    ('5500', '5000', 'التسويق', 'Marketing', 'EXPENSE', 'DEBIT', true, 270),
    ('5600', '5000', 'مصروف الإهلاك', 'Depreciation expense', 'EXPENSE', 'DEBIT', true, 280)
)
INSERT INTO account_template_accounts (template_id, code, parent_code, name_ar, name_en, account_type, normal_balance, allow_posting, sort_order)
SELECT t.id, a.code, a.parent_code, a.name_ar, a.name_en, a.account_type, a.normal_balance, a.allow_posting, a.sort_order
FROM account_templates t CROSS JOIN common_accounts a;

WITH industry_accounts(industry_code, code, parent_code, name_ar, name_en, account_type, normal_balance, allow_posting, sort_order) AS (
    VALUES
    ('TRADE', '1160', '1100', 'دفعات مقدمة للموردين', 'Supplier advances', 'ASSET', 'DEBIT', true, 300),
    ('TRADE', '5110', '5000', 'تكلفة البضاعة المباعة', 'Cost of goods sold', 'EXPENSE', 'DEBIT', true, 310),
    ('SERVICES', '1160', '1100', 'أعمال تحت التنفيذ', 'Unbilled service work', 'ASSET', 'DEBIT', true, 300),
    ('SERVICES', '5110', '5000', 'تكاليف تقديم الخدمات', 'Service delivery costs', 'EXPENSE', 'DEBIT', true, 310),
    ('CONTRACTING', '1160', '1100', 'تكاليف مشاريع تحت التنفيذ', 'Contract work in progress', 'ASSET', 'DEBIT', true, 300),
    ('CONTRACTING', '1170', '1100', 'مستخلصات محتجزة لدى العملاء', 'Contract retentions receivable', 'ASSET', 'DEBIT', true, 310),
    ('CONTRACTING', '2140', '2100', 'دفعات مقدمة من العملاء', 'Customer advances', 'LIABILITY', 'CREDIT', true, 320),
    ('CONTRACTING', '5110', '5000', 'تكاليف المشاريع', 'Project costs', 'EXPENSE', 'DEBIT', true, 330),
    ('RESTAURANTS', '1160', '1100', 'مواد غذائية ومستهلكات', 'Food materials and consumables', 'ASSET', 'DEBIT', true, 300),
    ('RESTAURANTS', '1170', '1100', 'مواد تعبئة وتغليف', 'Packaging materials', 'ASSET', 'DEBIT', true, 310),
    ('RESTAURANTS', '5110', '5000', 'تكلفة الأغذية والمشروبات', 'Food and beverage costs', 'EXPENSE', 'DEBIT', true, 320),
    ('MANUFACTURING', '1160', '1100', 'المواد الخام', 'Raw materials', 'ASSET', 'DEBIT', true, 300),
    ('MANUFACTURING', '1170', '1100', 'الإنتاج تحت التشغيل', 'Work in progress', 'ASSET', 'DEBIT', true, 310),
    ('MANUFACTURING', '1180', '1100', 'الإنتاج التام', 'Finished goods', 'ASSET', 'DEBIT', true, 320),
    ('MANUFACTURING', '5110', '5000', 'تكلفة الإنتاج المباع', 'Cost of goods manufactured and sold', 'EXPENSE', 'DEBIT', true, 330),
    ('REAL_ESTATE', '1160', '1100', 'مشروعات عقارية تحت التطوير', 'Real estate under development', 'ASSET', 'DEBIT', true, 300),
    ('REAL_ESTATE', '1170', '1100', 'عقارات محتفظ بها للبيع', 'Properties held for sale', 'ASSET', 'DEBIT', true, 310),
    ('REAL_ESTATE', '2140', '2100', 'دفعات مقدمة من المشترين', 'Buyer advances', 'LIABILITY', 'CREDIT', true, 320),
    ('REAL_ESTATE', '5110', '5000', 'تكلفة العقارات المباعة', 'Cost of properties sold', 'EXPENSE', 'DEBIT', true, 330)
)
INSERT INTO account_template_accounts (template_id, code, parent_code, name_ar, name_en, account_type, normal_balance, allow_posting, sort_order)
SELECT t.id, a.code, a.parent_code, a.name_ar, a.name_en, a.account_type, a.normal_balance, a.allow_posting, a.sort_order
FROM account_templates t JOIN industry_accounts a ON a.industry_code = t.industry_code;

CREATE TABLE companies (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL REFERENCES tenants(id) ON DELETE RESTRICT,
    name varchar(200) NOT NULL,
    base_currency char(3) NOT NULL DEFAULT 'SAR',
    tax_number varchar(32) NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, id)
);
CREATE INDEX ix_companies_tenant ON companies(tenant_id);

CREATE TABLE branches (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL,
    company_id uuid NOT NULL,
    code varchar(64) NOT NULL,
    name varchar(200) NOT NULL,
    is_active boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, company_id, id),
    UNIQUE (tenant_id, company_id, code),
    FOREIGN KEY (tenant_id, company_id) REFERENCES companies(tenant_id, id) ON DELETE RESTRICT
);

CREATE TABLE fiscal_years (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL,
    company_id uuid NOT NULL,
    name varchar(100) NOT NULL,
    start_date date NOT NULL,
    end_date date NOT NULL,
    status varchar(20) NOT NULL DEFAULT 'OPEN' CHECK (status IN ('OPEN','CLOSED')),
    CHECK (start_date <= end_date),
    UNIQUE (tenant_id, company_id, id),
    FOREIGN KEY (tenant_id, company_id) REFERENCES companies(tenant_id, id) ON DELETE RESTRICT
);

CREATE TABLE fiscal_periods (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL,
    company_id uuid NOT NULL,
    fiscal_year_id uuid NOT NULL,
    period_number integer NOT NULL CHECK (period_number > 0),
    start_date date NOT NULL,
    end_date date NOT NULL,
    status varchar(20) NOT NULL DEFAULT 'OPEN' CHECK (status IN ('OPEN','CLOSED','LOCKED')),
    CHECK (start_date <= end_date),
    UNIQUE (tenant_id, company_id, id),
    UNIQUE (tenant_id, company_id, fiscal_year_id, period_number),
    FOREIGN KEY (tenant_id, company_id, fiscal_year_id) REFERENCES fiscal_years(tenant_id, company_id, id) ON DELETE RESTRICT
);

CREATE TABLE accounts (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL,
    company_id uuid NOT NULL,
    parent_account_id uuid NULL,
    code varchar(64) NOT NULL,
    name_ar varchar(200) NOT NULL,
    name_en varchar(200) NOT NULL,
    account_type varchar(24) NOT NULL CHECK (account_type IN ('ASSET','LIABILITY','EQUITY','REVENUE','EXPENSE')),
    normal_balance varchar(6) NOT NULL CHECK (normal_balance IN ('DEBIT','CREDIT')),
    is_active boolean NOT NULL DEFAULT true,
    allow_posting boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, company_id, id),
    UNIQUE (tenant_id, company_id, code),
    CHECK (parent_account_id IS NULL OR parent_account_id <> id),
    FOREIGN KEY (tenant_id, company_id) REFERENCES companies(tenant_id, id) ON DELETE RESTRICT,
    FOREIGN KEY (tenant_id, company_id, parent_account_id) REFERENCES accounts(tenant_id, company_id, id) ON DELETE RESTRICT
);
CREATE INDEX ix_accounts_parent ON accounts(tenant_id, company_id, parent_account_id);

CREATE TABLE journal_entries (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL,
    company_id uuid NOT NULL,
    fiscal_period_id uuid NOT NULL,
    reversal_of_entry_id uuid NULL,
    entry_number varchar(80) NOT NULL,
    entry_date date NOT NULL,
    description varchar(1000) NOT NULL,
    currency_code char(3) NOT NULL DEFAULT 'SAR',
    status varchar(20) NOT NULL DEFAULT 'DRAFT' CHECK (status IN ('DRAFT','APPROVED','POSTED','CANCELLED')),
    idempotency_key varchar(200) NULL,
    idempotency_request_hash varchar(64) NULL,
    source_type varchar(64) NULL,
    source_id uuid NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    posted_at timestamptz NULL,
    UNIQUE (tenant_id, company_id, id),
    UNIQUE (tenant_id, company_id, entry_number),
    CHECK ((idempotency_key IS NULL) = (idempotency_request_hash IS NULL)),
    CHECK ((source_type IS NULL) = (source_id IS NULL)),
    CHECK (status <> 'POSTED' OR posted_at IS NOT NULL),
    FOREIGN KEY (tenant_id, company_id) REFERENCES companies(tenant_id, id) ON DELETE RESTRICT,
    FOREIGN KEY (tenant_id, company_id, fiscal_period_id) REFERENCES fiscal_periods(tenant_id, company_id, id) ON DELETE RESTRICT,
    FOREIGN KEY (tenant_id, company_id, reversal_of_entry_id) REFERENCES journal_entries(tenant_id, company_id, id) ON DELETE RESTRICT
);
CREATE UNIQUE INDEX ux_journal_idempotency ON journal_entries(tenant_id, company_id, idempotency_key) WHERE idempotency_key IS NOT NULL;
CREATE UNIQUE INDEX ux_journal_reversal ON journal_entries(tenant_id, company_id, reversal_of_entry_id) WHERE reversal_of_entry_id IS NOT NULL;
CREATE UNIQUE INDEX ux_journal_source ON journal_entries(tenant_id, company_id, source_type, source_id) WHERE source_id IS NOT NULL;
CREATE INDEX ix_journal_entries_date_status ON journal_entries(tenant_id, company_id, entry_date, status);

CREATE TABLE journal_entry_lines (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL,
    company_id uuid NOT NULL,
    journal_entry_id uuid NOT NULL,
    account_id uuid NOT NULL,
    line_number integer NOT NULL CHECK (line_number > 0),
    description varchar(1000) NOT NULL DEFAULT '',
    debit numeric(20,4) NOT NULL DEFAULT 0 CHECK (debit >= 0),
    credit numeric(20,4) NOT NULL DEFAULT 0 CHECK (credit >= 0),
    CHECK ((debit > 0 AND credit = 0) OR (credit > 0 AND debit = 0)),
    UNIQUE (tenant_id, company_id, journal_entry_id, line_number),
    FOREIGN KEY (tenant_id, company_id, journal_entry_id) REFERENCES journal_entries(tenant_id, company_id, id) ON DELETE RESTRICT,
    FOREIGN KEY (tenant_id, company_id, account_id) REFERENCES accounts(tenant_id, company_id, id) ON DELETE RESTRICT
);
CREATE INDEX ix_journal_lines_account ON journal_entry_lines(tenant_id, company_id, account_id);

-- Period locks serialize entry creation against period closing. This prevents a journal draft
-- from slipping into a period while another transaction closes it.
CREATE OR REPLACE FUNCTION guard_journal_entry_period() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    period_start date;
    period_end date;
    period_status text;
BEGIN
    SELECT start_date, end_date, status
      INTO period_start, period_end, period_status
      FROM fiscal_periods
     WHERE tenant_id = NEW.tenant_id
       AND company_id = NEW.company_id
       AND id = NEW.fiscal_period_id
     FOR UPDATE;
    IF NOT FOUND OR period_status <> 'OPEN'
       OR NEW.entry_date < period_start OR NEW.entry_date > period_end THEN
        RAISE EXCEPTION 'Fiscal period is not open or entry date is outside the period';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER trg_journal_entry_period_guard
    BEFORE INSERT OR UPDATE OF fiscal_period_id, entry_date ON journal_entries
    FOR EACH ROW EXECUTE FUNCTION guard_journal_entry_period();

-- Until exchange rates and base-currency amounts are implemented, entries must use the
-- company's base currency. This prevents trial balance from accidentally summing unlike currencies.
CREATE OR REPLACE FUNCTION guard_journal_entry_currency() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE base_currency char(3);
BEGIN
    SELECT c.base_currency INTO base_currency
      FROM companies c
     WHERE c.tenant_id = NEW.tenant_id AND c.id = NEW.company_id;
    IF NOT FOUND OR upper(trim(NEW.currency_code)) <> upper(trim(base_currency)) THEN
        RAISE EXCEPTION 'Journal entry currency must match company base currency until multi-currency support is enabled';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER trg_journal_entry_currency_guard
    BEFORE INSERT OR UPDATE OF currency_code ON journal_entries
    FOR EACH ROW EXECUTE FUNCTION guard_journal_entry_currency();

-- An open period may be closed only when no draft or approved entries remain. Once closed,
-- it cannot silently be re-opened; corrections use the documented period reopening procedure.
CREATE OR REPLACE FUNCTION guard_fiscal_period_close() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.status <> 'OPEN' AND NEW.status IS DISTINCT FROM OLD.status THEN
        RAISE EXCEPTION 'Closed or locked fiscal periods cannot be re-opened through ordinary updates';
    END IF;
    IF OLD.status = 'OPEN' AND NEW.status = 'CLOSED' AND EXISTS (
        SELECT 1 FROM journal_entries e
         WHERE e.tenant_id = OLD.tenant_id
           AND e.company_id = OLD.company_id
           AND e.fiscal_period_id = OLD.id
           AND e.status IN ('DRAFT', 'APPROVED')
    ) THEN
        RAISE EXCEPTION 'Fiscal period has draft or approved journal entries';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER trg_fiscal_period_close_guard
    BEFORE UPDATE OF status ON fiscal_periods
    FOR EACH ROW EXECUTE FUNCTION guard_fiscal_period_close();

-- Freeze entry headers once approved (except a status transition), and never mutate posted/cancelled entries.
CREATE OR REPLACE FUNCTION guard_journal_entry_mutation() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        IF OLD.status <> 'DRAFT' THEN
            RAISE EXCEPTION 'Only draft journal entries may be deleted';
        END IF;
        RETURN OLD;
    END IF;

    IF OLD.status IN ('POSTED', 'CANCELLED') THEN
        RAISE EXCEPTION 'Posted/cancelled journal entries are immutable; use a reversal/correction';
    END IF;

    IF OLD.status = 'APPROVED' AND
       ROW(NEW.tenant_id, NEW.company_id, NEW.fiscal_period_id, NEW.reversal_of_entry_id,
           NEW.entry_number, NEW.entry_date, NEW.description, NEW.currency_code,
           NEW.idempotency_key, NEW.idempotency_request_hash, NEW.source_type, NEW.source_id, NEW.created_at)
       IS DISTINCT FROM
       ROW(OLD.tenant_id, OLD.company_id, OLD.fiscal_period_id, OLD.reversal_of_entry_id,
           OLD.entry_number, OLD.entry_date, OLD.description, OLD.currency_code,
           OLD.idempotency_key, OLD.idempotency_request_hash, OLD.source_type, OLD.source_id, OLD.created_at) THEN
        RAISE EXCEPTION 'Approved journal entry content is frozen';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER trg_journal_entry_mutation
    BEFORE UPDATE OR DELETE ON journal_entries
    FOR EACH ROW EXECUTE FUNCTION guard_journal_entry_mutation();

-- Database backstop for the central accounting invariants; the application must repeat these checks.
CREATE OR REPLACE FUNCTION enforce_journal_entry_state() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    period_start date;
    period_end date;
    period_status text;
    line_count bigint;
    total_debit numeric(30,4);
    total_credit numeric(30,4);
BEGIN
    IF OLD.status IN ('POSTED', 'CANCELLED') THEN
        RAISE EXCEPTION 'Posted/cancelled journal entry status cannot be changed';
    END IF;
    IF NEW.status = OLD.status THEN
        RETURN NEW;
    END IF;
    IF NEW.status = 'APPROVED' AND OLD.status <> 'DRAFT' THEN
        RAISE EXCEPTION 'Only a draft journal entry can be approved';
    ELSIF NEW.status = 'CANCELLED' AND OLD.status NOT IN ('DRAFT', 'APPROVED') THEN
        RAISE EXCEPTION 'Only a draft or approved journal entry can be cancelled';
    ELSIF NEW.status = 'POSTED' AND OLD.status <> 'APPROVED' THEN
        RAISE EXCEPTION 'Only an approved journal entry can be posted';
    ELSIF NEW.status NOT IN ('DRAFT', 'APPROVED', 'POSTED', 'CANCELLED') THEN
        RAISE EXCEPTION 'Invalid journal entry status';
    END IF;

    IF NEW.status IN ('APPROVED', 'POSTED') THEN
        SELECT start_date, end_date, status
          INTO period_start, period_end, period_status
          FROM fiscal_periods
         WHERE tenant_id = NEW.tenant_id
           AND company_id = NEW.company_id
           AND id = NEW.fiscal_period_id
         FOR UPDATE;
        IF NOT FOUND OR period_status <> 'OPEN'
           OR NEW.entry_date < period_start OR NEW.entry_date > period_end THEN
            RAISE EXCEPTION 'Fiscal period is not open or entry date is outside the period';
        END IF;

        SELECT count(*), COALESCE(sum(debit), 0), COALESCE(sum(credit), 0)
          INTO line_count, total_debit, total_credit
          FROM journal_entry_lines
         WHERE tenant_id = NEW.tenant_id
           AND company_id = NEW.company_id
           AND journal_entry_id = NEW.id;
        IF line_count < 2 OR total_debit <> total_credit THEN
            RAISE EXCEPTION 'Journal entry must have at least two lines and equal debit/credit totals';
        END IF;
        IF EXISTS (
            SELECT 1
              FROM journal_entry_lines l
              JOIN accounts a ON a.tenant_id = l.tenant_id
                             AND a.company_id = l.company_id
                             AND a.id = l.account_id
             WHERE l.tenant_id = NEW.tenant_id
               AND l.company_id = NEW.company_id
               AND l.journal_entry_id = NEW.id
               AND (a.is_active = false OR a.allow_posting = false)
        ) THEN
            RAISE EXCEPTION 'Journal entry contains an inactive or non-postable account';
        END IF;
    END IF;

    IF NEW.status = 'POSTED' THEN
        NEW.posted_at := COALESCE(NEW.posted_at, now());
    ELSE
        NEW.posted_at := NULL;
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER trg_journal_entry_state
    BEFORE UPDATE OF status ON journal_entries
    FOR EACH ROW EXECUTE FUNCTION enforce_journal_entry_state();

CREATE OR REPLACE FUNCTION guard_journal_entry_line_mutation() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    parent_status text;
    target_tenant uuid;
    target_company uuid;
    target_entry uuid;
BEGIN
    IF TG_OP = 'UPDATE' AND
       (NEW.tenant_id IS DISTINCT FROM OLD.tenant_id OR
        NEW.company_id IS DISTINCT FROM OLD.company_id OR
        NEW.journal_entry_id IS DISTINCT FROM OLD.journal_entry_id) THEN
        RAISE EXCEPTION 'A journal line cannot be moved to another tenant, company, or entry';
    END IF;

    IF TG_OP = 'INSERT' THEN
        target_tenant := NEW.tenant_id;
        target_company := NEW.company_id;
        target_entry := NEW.journal_entry_id;
    ELSE
        target_tenant := OLD.tenant_id;
        target_company := OLD.company_id;
        target_entry := OLD.journal_entry_id;
    END IF;

    SELECT status INTO parent_status
      FROM journal_entries
     WHERE tenant_id = target_tenant
       AND company_id = target_company
       AND id = target_entry
     FOR UPDATE;
    IF NOT FOUND OR parent_status <> 'DRAFT' THEN
        RAISE EXCEPTION 'Journal lines can be changed only while the parent entry is a draft';
    END IF;

    IF TG_OP = 'DELETE' THEN
        RETURN OLD;
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER trg_journal_entry_line_mutation
    BEFORE INSERT OR UPDATE OR DELETE ON journal_entry_lines
    FOR EACH ROW EXECUTE FUNCTION guard_journal_entry_line_mutation();

CREATE TABLE audit_logs (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL REFERENCES tenants(id) ON DELETE RESTRICT,
    user_id uuid NULL REFERENCES users(id) ON DELETE RESTRICT,
    action varchar(100) NOT NULL,
    entity_type varchar(100) NOT NULL,
    entity_id varchar(200) NULL,
    ip_address varchar(64) NULL,
    details_json jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_audit_logs_tenant_created ON audit_logs(tenant_id, created_at DESC);

CREATE OR REPLACE FUNCTION reject_audit_log_mutation() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'Audit log rows are append-only';
END $$;
CREATE TRIGGER trg_audit_logs_append_only
    BEFORE UPDATE OR DELETE ON audit_logs
    FOR EACH ROW EXECUTE FUNCTION reject_audit_log_mutation();

-- RLS applies to every tenant-owned table in this bootstrap. app.tenant_id must be set
-- using set_config('app.tenant_id', ..., true) in the same transaction as data access.
CREATE OR REPLACE FUNCTION app_current_tenant_id() RETURNS uuid
LANGUAGE sql STABLE AS $$
    SELECT NULLIF(current_setting('app.tenant_id', true), '')::uuid
$$;

ALTER TABLE tenants ENABLE ROW LEVEL SECURITY;
ALTER TABLE tenants FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON tenants
    USING (id = app_current_tenant_id())
    WITH CHECK (id = app_current_tenant_id());

DO $$
DECLARE table_name text;
BEGIN
    FOREACH table_name IN ARRAY ARRAY[
        'tenant_memberships','tenant_subscriptions','companies','branches','fiscal_years',
        'fiscal_periods','accounts','journal_entries','journal_entry_lines','audit_logs'
    ] LOOP
        EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY', table_name);
        EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY', table_name);
        EXECUTE format('CREATE POLICY tenant_isolation ON %I USING (tenant_id = app_current_tenant_id()) WITH CHECK (tenant_id = app_current_tenant_id())', table_name);
    END LOOP;
END $$;

COMMIT;
