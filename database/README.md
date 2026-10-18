# Database deployment notes

## Roles and privileges

- Apply the bootstrap SQL using a dedicated schema-migration owner on a **new database**.
- The runtime API role must not be `SUPERUSER`, have `BYPASSRLS`, or own the tenant tables. PostgreSQL superusers bypass row-level security even if `FORCE ROW LEVEL SECURITY` is set.
- Grant only the runtime role the minimum DML rights needed by the API. For example, after reviewing the actual permission set, an administrator may grant `CONNECT`, `USAGE ON SCHEMA`, and table-level `SELECT/INSERT/UPDATE/DELETE` privileges for operational tables. Revoke `UPDATE` and `DELETE` from `audit_logs`; it should be insert/select only. Keep the schema-owner credentials out of the app environment.
- Use reviewed `ALTER DEFAULT PRIVILEGES` for future migration-created objects. Do not give the API credentials migration or superuser permissions.
- `account_templates` and `account_template_accounts` are shared reference tables, not tenant-owned business tables. Grant the runtime API role `SELECT` on these tables only; perform changes to template reference data via reviewed schema/data deployments.

## Row-level security

- `tenants` is RLS-protected by `id`; tenant-owned tables use `tenant_id` policies.
- The request pipeline resolves a validated OIDC `sub` to an enabled internal user, reads the active membership/role while scoping the membership query, and rejects inactive/nonmember users.
- Tenant operations run through `TenantTransactionRunner`, which sets `app.tenant_id` using transaction-local `set_config(..., true)` and executes operations inside the same database transaction.
- `X-Tenant-Id` is only a candidate selector. It never grants access without a matching active membership.
- Do not add direct tenant-table queries outside the transaction runner. Any new route must use the same validated tenant context and authorization checks.

## Provisioning

`seeds/first-tenant-provisioning-template.sql` is a protected operator template. Replace every value with a real tenant GUID, tenant slug/name, and exact OIDC `sub`/email/name. Do not expose operator credentials, the SQL template, or database connectivity to desktop clients. Self-service SaaS registration and subscription billing are not yet implemented.

## Migration validation

The initial schema is still a bootstrap script, not a complete versioned EF Core migration chain. Apply it only to a new, disposable PostgreSQL database until a tested forward-upgrade path is supplied. The SQL and RLS policies have not been executed against PostgreSQL in this environment. Before production, run clean-install, rerun/upgrade, rollback or restore, two-tenant isolation, concurrent idempotency, journal immutability, and runtime-role permission tests against real PostgreSQL.

## Automated PostgreSQL smoke tests

The GitHub Actions workflow starts a disposable PostgreSQL 17 service, applies the bootstrap schema, creates a non-owner/non-superuser runtime role, and seeds two isolated tenants. It then runs `database/tests/postgres-smoke.sql` as that restricted role to verify that missing tenant context returns no tenant-owned rows, tenant A cannot read tenant B's company, a balanced journal can be approved and posted, an unbalanced journal cannot be approved, debit and credit cannot both be positive on one line, and a posted journal cannot be edited.

Run the same checks locally from a shell with `psql` installed and an empty disposable database by applying the migration, then the preparation and fixture SQL files in order. Do not run these test fixtures against a production database.
