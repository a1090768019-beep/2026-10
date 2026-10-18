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
- `IsEntitled` is informational only. The business API does not yet block requests based on subscription status or enforce plan quotas.
- No checkout, payment processing, provider webhook, automated renewal, grace-period policy, refund handling, or customer billing portal is implemented.
- The database does not seed commercial prices or a default paid plan. A trusted platform operator must configure `subscription_plans` and provision a `tenant_subscriptions` row through a reviewed administrative process.
- `limits_json` is returned as configured reference data; company/user/document counts are not yet measured or enforced.
- Do not treat `ACTIVE` as proof that a payment was collected. Payment-provider reconciliation and audit handling must be added before commercial billing.

## Next implementation gates

1. Choose a payment provider and document Saudi payment, refund, invoice, and reconciliation requirements.
2. Build platform-operator workflows for plan publishing, subscription assignment, cancellation, and auditable plan changes.
3. Build tenant onboarding and invitations only after the identity provider and secure account-recovery process are selected.
4. Enforce usage limits and define an explicit grace/suspension policy only after product and legal approval.
5. Add webhook signature verification, idempotent event processing, replay tests, and PostgreSQL integration tests for the whole subscription lifecycle.
