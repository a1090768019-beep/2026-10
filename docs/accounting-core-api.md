# Accounting Core API (implementation status)

This is the first operational vertical slice for the C# / ASP.NET Core backend. It is not a claim that the overall ERP is production-ready.

## Security model

- All `/api/*` routes require a validated bearer identity and `X-Tenant-Id`.
- `sub` from the validated OIDC token is matched against `users.oidc_subject`; disabled or unprovisioned users are denied.
- The selected tenant is checked against an active `tenant_memberships` row. The tenant header is never itself authorization.
- Role codes are `OWNER`, `ADMIN`, `ACCOUNTANT`, and `VIEWER`; only the server-loaded membership role is used.
- Business operations run through `TenantTransactionRunner`, which sets the transaction-local PostgreSQL RLS tenant context and keeps reads/writes in one transaction.
- `VIEWER` can read reports and records, but cannot create or post accounting records.

The product still needs an identity-provider configuration, secure tenant provisioning, role-management workflows, and a full authorization matrix before production sale.

## Implemented routes

All routes are under `/api/v1`.

| Method | Route | Purpose |
|---|---|---|
| GET | `/companies` | List current tenant's companies |
| POST | `/companies` | Create a company (OWNER/ADMIN) |
| GET | `/account-templates` | List active industry chart templates stored in PostgreSQL reference tables |
| POST | `/companies/{companyId}/accounts/apply-template` | Apply a template to an empty company chart; OWNER/ADMIN only; never overwrites existing accounts |
| GET | `/companies/{companyId}/accounts` | Chart of accounts |
| POST | `/companies/{companyId}/accounts` | Create an account |
| GET/POST | `/companies/{companyId}/fiscal-years` | List/create fiscal year; creation also creates calendar-based periods |
| GET | `/companies/{companyId}/fiscal-periods` | List periods; optionally filter with `fiscalYearId` |
| GET/POST | `/companies/{companyId}/journal-entries` | List/create a draft double-entry journal |
| GET | `/journal-entries/{entryId}` | Read journal entry and lines |
| POST | `/journal-entries/{entryId}/approve` | Validate and approve a draft |
| POST | `/journal-entries/{entryId}/post` | Post an approved entry |
| POST | `/journal-entries/{entryId}/cancel` | Cancel a draft or approved entry |
| POST | `/journal-entries/{entryId}/reverse` | Create and post a reversing entry |
| GET | `/companies/{companyId}/reports/trial-balance?fromDate=YYYY-MM-DD&toDate=YYYY-MM-DD` | Trial balance movements for the requested range and balances through the ending date |

Journal amounts use .NET `decimal` and PostgreSQL `numeric(20,4)`. Journal creation enforces at least two valid lines, exact debit/credit equality, active/postable accounts, currency format, a valid open fiscal period, and an optional idempotency key with a request fingerprint. Posted entries cannot be edited; corrections use reversals.

## Manual provisioning prerequisites

A platform operator must provision a tenant, create a row in `users` for the exact `sub` claim from the approved identity provider, and create an active membership with an appropriate role. See `database/seeds/first-tenant-provisioning-template.sql`. This is deliberately not exposed as a public self-signup endpoint yet.

## Not implemented yet

The desktop WPF shell is not yet wired to these business endpoints and does not yet contain OIDC login. There is no tenant self-registration/subscription billing, no complete role-permission editor, no invoices/inventory/payments, no completed Saudi e-invoicing integration, and no production deployment/pentest evidence. These are release blockers for commercial launch.


## Industry chart template behavior

The bootstrap migration seeds six templates: trade, services, contracting, restaurants, manufacturing, and real estate. Template records are shared platform reference data and are not governed by tenant RLS; the runtime role should have read-only access. Applying a template creates account records in the selected tenant/company transaction and records an audit entry. The endpoint refuses to run if the company already has any account rows, avoiding destructive or ambiguous merging.

The templates are starter charts, not authoritative accounting policy. Review account names, codes, VAT treatment, cost recognition, and regulatory obligations with an accounting professional. Specialized manufacturing, construction, restaurant, and real-estate costing workflows are not implemented merely by seeding these account names.

## Concurrent write safety

Journal creation obtains PostgreSQL transaction-scoped advisory locks for the normalized tenant/company entry number and, when supplied, the idempotency key before checking for an existing record. Retries with the same key can therefore read the committed first result instead of racing into a unique-index exception; concurrent reuse of the same entry number is returned as a regular conflict. Reversal requests serialize on the original entry before checking whether a reversal already exists. Fiscal-year creation serializes overlap checks per company.

These locks complement, rather than replace, the database unique indexes and triggers. The CI PostgreSQL smoke tests exercise the database enforcement layer; the lock behavior is also covered by source-contract checks. A dedicated high-concurrency API load test remains a release-hardening task.


## شريحة العملاء ومسودات المبيعات
طبّق 0002_sales_invoice_drafts.sql بعد bootstrap. مسارات العملاء تعرض/تنشئ العملاء، ومسارات sales-invoice-drafts تعرض/تنشئ مسودات داخلية ببنود محفوظة ومجموع متحقق منه. تستخدم قاعدة البيانات RLS ومفاتيح مركبة وقيدًا مؤجلًا لمطابقة الإجمالي. لا توجد VAT أو إصدار قانوني نهائي أو GL posting أو تحصيل أو إشعارات دائنة؛ لا تستخدم المسودة كفاتورة ضريبية.


## معاينة ضريبة القيمة المضافة (غير محفوظة)
- `POST /api/v1/companies/{companyId}/sales-vat-preview` يقبل بنودًا فيها الوصف والكمية وسعر الوحدة والخصم والتصنيف الضريبي، وسبب المعالجة للحالات غير القياسية.
- التصنيفات: `StandardRated`, `ZeroRated`, `Exempt`، `OutsideScope`. أي تصنيف غير قياسي يتطلب سببًا نصيًا. الحساب يستخدم `decimal`، ويقرّب قيمة البند والضريبة إلى منزلتين عشريتين باستخدام `MidpointRounding.AwayFromZero`.
- `documentStatus=PREVIEW_ONLY`: لا تُحفظ المعاينة ولا تمثل فاتورة ضريبية أو دليل توافق ZATCA. لم تُضف أعمدة ضريبة للمسودات المحفوظة، ولا يوجد ترحيل ضريبة/إيراد/ذمم أو إصدار نهائي.
- يُطبّق 15% فقط على البند المصنف StandardRated صراحةً. يجب مراجعة تصنيف كل توريد وقواعد الهيئة قبل أي استخدام قانوني.


## حفظ حساب VAT على مسودة بيع (migration 0003)

- `POST /api/v1/companies/{companyId}/sales-invoice-drafts/{invoiceId}/calculate-vat` يستقبل لكل بند `lineNumber`, `discountAmount`, `treatment`, و`treatmentReason` عند الحاجة.
- الخادم لا يثق في مبالغ/كميات/أسعار ضريبية من العميل؛ يقرأ الكمية والوصف والسعر من البنود المحفوظة ويحسب بواسطة `SaudiVatCalculator`.
- تُحدّث حقول تصنيف/خصم/VAT للبنود، وإجماليات الرأس، وسجل التدقيق ضمن معاملة PostgreSQL واحدة. قيود قاعدة البيانات المؤجلة تطابق إجماليات الرأس مع مجموع البنود.
- `GET /api/v1/companies/{companyId}/sales-invoice-drafts` يعرض `taxCalculationStatus` وVAT والإجمالي إن كانت الحسابات محفوظة. `NOT_CALCULATED` تعني غير مصنفة؛ `CALCULATED` تعني حسابًا داخليًا محفوظًا فقط.
- يمكن إعادة الحساب طالما بقيت الحالة `DRAFT`. لا يوجد إصدار نهائي/منع تعديل بعد الإصدار، ولا قيد ذمم/إيراد/ضريبة في GL، ولا تحصيل، ولا XML/QR/توقيع أو إرسال/إقرار ZATCA. لا تُسلّم المسودة كفاتورة قانونية.
