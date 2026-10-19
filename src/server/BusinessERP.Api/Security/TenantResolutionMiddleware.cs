using BusinessERP.Infrastructure.Persistence;
using BusinessERP.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace BusinessERP.Api.Security;

/// <summary>
/// Resolves X-Tenant-Id only after token validation and verifies that the token subject maps to an
/// enabled local user with active membership. The header is a selector, never a grant of access.
/// </summary>
public sealed class TenantResolutionMiddleware(RequestDelegate next, ILogger<TenantResolutionMiddleware> logger)
{
    public async Task InvokeAsync(
        HttpContext http,
        IConfiguration configuration)
    {
        var cancellationToken = http.RequestAborted;
        if (!http.Request.Path.StartsWithSegments("/api"))
        {
            await next(http);
            return;
        }

        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("Postgres")))
        {
            http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await http.Response.WriteAsJsonAsync(new { error = "database_not_configured", message = "Configure ConnectionStrings:Postgres." }, cancellationToken);
            return;
        }

        if (http.User.Identity?.IsAuthenticated != true)
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await http.Response.WriteAsJsonAsync(new { error = "authentication_required" }, cancellationToken);
            return;
        }

        var subject = http.User.FindFirst("sub")?.Value
            ?? http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrWhiteSpace(subject))
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await http.Response.WriteAsJsonAsync(new { error = "identity_subject_missing" }, cancellationToken);
            return;
        }

        if (!Guid.TryParse(http.Request.Headers["X-Tenant-Id"].FirstOrDefault(), out var tenantId) || tenantId == Guid.Empty)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsJsonAsync(new { error = "valid_x_tenant_id_header_required" }, cancellationToken);
            return;
        }

        // Always resolve scoped services from the active request scope. This keeps the
        // TenantContext instance populated here identical to the one injected into API endpoints.
        var services = http.RequestServices;
        var db = services.GetRequiredService<ErpDbContext>();
        var user = await db.Users.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OidcSubject == subject && x.DisabledAt == null, cancellationToken);
        if (user is null)
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            await http.Response.WriteAsJsonAsync(new { error = "user_not_provisioned" }, cancellationToken);
            return;
        }

        var verifier = services.GetRequiredService<TenantMembershipVerifier>();
        var roleCode = await verifier.GetActiveRoleAsync(tenantId, user.Id, cancellationToken);
        if (roleCode is null)
        {
            logger.LogWarning("Denied tenant access for local user {UserId} to tenant {TenantId}", user.Id, tenantId);
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            await http.Response.WriteAsJsonAsync(new { error = "active_tenant_membership_required" }, cancellationToken);
            return;
        }

        services.GetRequiredService<TenantContext>().Set(tenantId, user.Id, roleCode);

        // Keep the plan catalogue and subscription-status views available so a tenant can
        // understand why access is suspended. All business API routes require a current
        // ACTIVE/TRIALING subscription; payment collection itself is verified only by a
        // future reviewed billing-provider integration.
        var requestPath = http.Request.Path.Value?.TrimEnd('/');
        var isSubscriptionRead =
            HttpMethods.IsGet(http.Request.Method) &&
            (string.Equals(requestPath, "/api/v1/subscription", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(requestPath, "/api/v1/subscription-plans", StringComparison.OrdinalIgnoreCase));

        if (!isSubscriptionRead)
        {
            // Subscription rows are protected by PostgreSQL RLS, so this lookup must use the
            // same transaction-local tenant context as all other tenant data access.
            var transactionRunner = services.GetRequiredService<TenantTransactionRunner>();
            var subscriptionState = await transactionRunner.ExecuteAsync(async (tenantDb, token) =>
            {
                var subscription = await tenantDb.TenantSubscriptions.AsNoTracking()
                    .Where(x => x.TenantId == tenantId)
                    .OrderByDescending(x => x.StartsAt)
                    .ThenByDescending(x => x.CreatedAt)
                    .Select(x => new { x.Status, x.StartsAt, x.EndsAt })
                    .FirstOrDefaultAsync(token);

                var now = DateTime.UtcNow;
                var withinWindow = subscription is not null &&
                    subscription.StartsAt <= now &&
                    (!subscription.EndsAt.HasValue || subscription.EndsAt.Value > now);
                var entitled = withinWindow && subscription is not null &&
                    (subscription.Status.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase) ||
                     subscription.Status.Equals("TRIALING", StringComparison.OrdinalIgnoreCase));
                return (HasSubscription: subscription is not null, IsEntitled: entitled);
            }, cancellationToken);

            if (!subscriptionState.IsEntitled)
            {
                http.Response.StatusCode = StatusCodes.Status402PaymentRequired;
                await http.Response.WriteAsJsonAsync(new
                {
                    error = "subscription_required_or_inactive",
                    message = !subscriptionState.HasSubscription
                        ? "لا يوجد اشتراك مخصص لهذا العميل. راجع حالة الاشتراك أو تواصل مع مشغل المنصة."
                        : "الاشتراك غير نشط أو انتهت مدته. راجع حالة الاشتراك أو تواصل مع مشغل المنصة."
                }, cancellationToken);
                return;
            }
        }

        await next(http);
    }
}
