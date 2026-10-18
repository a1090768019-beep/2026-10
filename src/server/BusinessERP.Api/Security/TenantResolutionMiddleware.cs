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
        await next(http);
    }
}
