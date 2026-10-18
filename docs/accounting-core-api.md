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
