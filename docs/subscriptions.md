# SaaS subscriptions — implementation status

## Read-only endpoints

All routes require an authenticated OIDC identity, an active tenant membership, and the normal tenant-selection header.

| Method | Route | Purpose |
|---|---|---|
| GET | `/api/v1/subscription-plans` | List active plan catalog rows configured by the platform operator |
| GET | `/api/v1/subscription` | Read the latest tenant subscription, plan, dates, status, and configured limits |

The current-subscription response reports `HasSubscription` and an informational `IsEntitled` calculation. It considers the latest subscription by start date and reports entitlement only when the record is `ACTIVE` or `TRIALING` and its start/end window includes the current UTC time.

## Explicit limitations

- These endpoints are read-only. They do not create a tenant, change plans, or write subscription rows.
- The tenant-resolution middleware enforces a minimum subscription gate for business `/api` routes: the latest tenant subscription must be `ACTIVE` or `TRIALING` and within its start/end window. `GET /api/v1/subscription` and `GET /api/v1/subscription-plans` remain available to explain the status. This gate does not prove payment collection.
- The `companies` plan limit is enforced transactionally by the API with a per-tenant advisory lock. The `entriesPerMonth` plan limit is enforced by a PostgreSQL `BEFORE INSERT` trigger on `journal_entries`, covering manual and document-linked posting flows; over-limit attempts return HTTP 409. A missing JSON limit property means no cap is configured. Limits such as `users` remain unenforced because complete user-administration/onboarding flows are not yet implemented.
- No checkout, payment processing, provider webhook, automated renewal, grace-period policy, refund handling, or customer billing portal is implemented.
- The database does not seed commercial prices or a default paid plan. A trusted platform operator must configure `subscription_plans` and provision a `tenant_subscriptions` row through a reviewed administrative process.

- Do not treat `ACTIVE` as proof that a payment was collected. Payment-provider reconciliation and audit handling must be added before commercial billing.

## Next implementation gates

1. Choose a payment provider and document Saudi payment, refund, invoice, and reconciliation requirements.
2. Build platform-operator workflows for plan publishing, subscription assignment, cancellation, and auditable plan changes.
3. Build tenant onboarding and invitations only after the identity provider and secure account-recovery process are selected.
4. Implement and test remaining limits (notably active-user counts) and define an explicit grace/suspension policy only after product and legal approval.
5. Add webhook signature verification, idempotent event processing, replay tests, and PostgreSQL integration tests for the whole subscription lifecycle.
