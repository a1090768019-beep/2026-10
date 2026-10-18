-- TEMPLATE ONLY. Run with psql after replacing values with real values from your
-- approved identity provider. Use a protected admin connection, never expose it to the app.
-- Example: psql "$ADMIN_DATABASE_URL" -v tenant_id='...' -v tenant_slug='...' \
--   -v tenant_name='...' -v user_subject='...' -v user_email='...' \
--   -v user_name='...' -v membership_role='OWNER' -f first-tenant-provisioning-template.sql

\if :{?tenant_id}\else
  \echo 'Missing psql variable: tenant_id'
  \quit 3
\endif
\if :{?tenant_slug}\else
  \echo 'Missing psql variable: tenant_slug'
  \quit 3
\endif
\if :{?tenant_name}\else
  \echo 'Missing psql variable: tenant_name'
  \quit 3
\endif
\if :{?user_subject}\else
  \echo 'Missing psql variable: user_subject (OIDC sub claim)'
  \quit 3
\endif
\if :{?user_email}\else
  \echo 'Missing psql variable: user_email'
  \quit 3
\endif
\if :{?user_name}\else
  \echo 'Missing psql variable: user_name'
  \quit 3
\endif
\if :{?membership_role}\else
  \set membership_role 'OWNER'
\endif

BEGIN;
SELECT set_config('app.tenant_id', :'tenant_id', true);

INSERT INTO tenants (id, slug, name, status)
VALUES (:'tenant_id'::uuid, :'tenant_slug', :'tenant_name', 'ACTIVE');

INSERT INTO users (oidc_subject, email, display_name)
VALUES (:'user_subject', :'user_email', :'user_name')
ON CONFLICT (oidc_subject) DO UPDATE
SET email = EXCLUDED.email,
    display_name = EXCLUDED.display_name,
    disabled_at = NULL;

INSERT INTO tenant_memberships (tenant_id, user_id, status, role_code)
SELECT :'tenant_id'::uuid, u.id, 'ACTIVE', :'membership_role'
FROM users u
WHERE u.oidc_subject = :'user_subject'
ON CONFLICT (tenant_id, user_id) DO UPDATE
SET status = 'ACTIVE', role_code = EXCLUDED.role_code;

COMMIT;
