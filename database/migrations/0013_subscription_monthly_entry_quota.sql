-- Enforce the configured per-tenant monthly journal-entry quota at the database boundary.
-- The trigger covers manual journals and all document-linked posting flows without relying on
-- each API endpoint remembering to call an application-only quota helper.
BEGIN;

CREATE OR REPLACE FUNCTION enforce_tenant_monthly_journal_entry_quota()
RETURNS trigger
LANGUAGE plpgsql
AS $function$
DECLARE
    plan_limits jsonb;
    subscription_status text;
    subscription_starts_at timestamptz;
    subscription_ends_at timestamptz;
    quota_text text;
    monthly_limit bigint;
    inserted_at timestamptz;
    month_start timestamptz;
    month_end timestamptz;
    entries_used bigint;
    lock_key text;
BEGIN
    -- created_at is server-owned metadata. Use the actual database insertion time so callers
    -- cannot bypass a monthly quota by supplying a backdated created_at value.
    inserted_at := clock_timestamp();
    NEW.created_at := inserted_at;

    SELECT p.limits_json, s.status, s.starts_at, s.ends_at
      INTO plan_limits, subscription_status, subscription_starts_at, subscription_ends_at
      FROM tenant_subscriptions s
      JOIN subscription_plans p ON p.id = s.subscription_plan_id
     WHERE s.tenant_id = NEW.tenant_id
     ORDER BY s.starts_at DESC, s.created_at DESC
     LIMIT 1;

    IF NOT FOUND
       OR subscription_status NOT IN ('ACTIVE', 'TRIALING')
       OR subscription_starts_at > inserted_at
       OR (subscription_ends_at IS NOT NULL AND subscription_ends_at <= inserted_at) THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P0001',
            MESSAGE = 'subscription_required_or_inactive';
    END IF;

    -- A missing limit means no monthly cap was configured for this plan.
    quota_text := plan_limits ->> 'entriesPerMonth';
    IF quota_text IS NULL THEN
        RETURN NEW;
    END IF;

    IF quota_text !~ '^(0|[1-9][0-9]*)$' THEN
        RAISE EXCEPTION USING
            ERRCODE = '22023',
            MESSAGE = 'Invalid subscription plan limit: entriesPerMonth must be a non-negative integer.';
    END IF;
    monthly_limit := quota_text::bigint;

    month_start := date_trunc('month', inserted_at AT TIME ZONE 'UTC') AT TIME ZONE 'UTC';
    month_end := month_start + INTERVAL '1 month';
    lock_key := 'tenant-journal-entries-month:' || NEW.tenant_id::text || ':' ||
                to_char(month_start AT TIME ZONE 'UTC', 'YYYY-MM');

    -- Serialize inserts for the same tenant and UTC month. Counts are taken only after the
    -- transaction-scoped advisory lock is acquired, preventing parallel posting requests from
    -- consuming the same final quota slot.
    PERFORM pg_advisory_xact_lock(hashtextextended(lock_key, 0));

    SELECT count(*)
      INTO entries_used
      FROM journal_entries
     WHERE tenant_id = NEW.tenant_id
       AND created_at >= month_start
       AND created_at < month_end;

    IF entries_used >= monthly_limit THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P0001',
            MESSAGE = 'subscription_quota_exceeded:entriesPerMonth',
            DETAIL = format('limit=%s;used=%s;period=%s',
                            monthly_limit, entries_used,
                            to_char(month_start AT TIME ZONE 'UTC', 'YYYY-MM'));
    END IF;

    RETURN NEW;
END
$function$;

CREATE TRIGGER trg_journal_entries_monthly_subscription_quota
    BEFORE INSERT ON journal_entries
    FOR EACH ROW
    EXECUTE FUNCTION enforce_tenant_monthly_journal_entry_quota();

COMMIT;
