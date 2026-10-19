using System.Text.Json;
using BusinessERP.Application.Abstractions;
using BusinessERP.Infrastructure.Persistence;
using BusinessERP.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace BusinessERP.Api.Endpoints;

/// <summary>
/// Tenant membership administration for users that have already been provisioned by the trusted
/// identity onboarding process. This API never creates identity-provider accounts or sends invites.
/// Membership changes are audited and the active-user plan limit is enforced inside the transaction.
/// </summary>
public static class TenantMembershipEndpoints
{
    private static readonly string[] ManagerRoles = ["OWNER", "ADMIN"];
    private static readonly HashSet<string> AllowedRoles = ["OWNER", "ADMIN", "ACCOUNTANT", "VIEWER"];
    private static readonly HashSet<string> AllowedStatuses = ["ACTIVE", "SUSPENDED", "REMOVED"];

    public static IEndpointRouteBuilder MapTenantMembershipEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1/memberships").WithTags("Tenant membership administration");
        api.MapGet("", ListMembers).WithName("ListTenantMembers");
        api.MapPost("", AssignMember).WithName("AssignTenantMember");
        api.MapPut("/{userId:guid}", UpdateMember).WithName("UpdateTenantMember");
        return endpoints;
    }

    private static async Task<IResult> ListMembers(
        ITenantContext tenant,
        TenantTransactionRunner runner,
        CancellationToken ct)
    {
        if (!HasRole(tenant.RoleCode, ManagerRoles))
            return Results.Forbid();

        return await runner.ExecuteAsync<IResult>(async (db, token) =>
        {
            var members = await (
                from membership in db.TenantMemberships.AsNoTracking()
                join user in db.Users.AsNoTracking() on membership.UserId equals user.Id
                where membership.TenantId == tenant.TenantId
                orderby membership.Status, user.DisplayName, user.Email
                select new TenantMemberDto(
                    membership.UserId,
                    user.DisplayName,
                    user.Email,
                    membership.RoleCode,
                    membership.Status,
                    membership.CreatedAt,
                    user.DisabledAt != null))
                .Take(500)
                .ToListAsync(token);

            return Results.Ok(members);
        }, ct);
    }

    private static async Task<IResult> AssignMember(
        AssignMembershipRequest request,
        ITenantContext tenant,
        HttpContext http,
        TenantTransactionRunner runner,
        CancellationToken ct)
    {
        if (!HasRole(tenant.RoleCode, ManagerRoles))
            return Results.Forbid();

        var role = NormalizeRole(request.RoleCode);
        if (role is null)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["roleCode"] = ["الدور يجب أن يكون OWNER أو ADMIN أو ACCOUNTANT أو VIEWER."]
            });

        if (tenant.RoleCode.Equals("ADMIN", StringComparison.OrdinalIgnoreCase) &&
            role is "OWNER" or "ADMIN")
        {
            return Results.Forbid();
        }

        if (request.UserId == Guid.Empty)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["userId"] = ["معرّف المستخدم مطلوب."]
            });

        return await runner.ExecuteAsync<IResult>(async (db, token) =>
        {
            await AcquireTenantMembershipLockAsync(db, tenant.TenantId, token);

            var targetUser = await db.Users.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == request.UserId && x.DisabledAt == null, token);
            if (targetUser is null)
                return Results.NotFound(new { error = "provisioned_user_not_found" });

            var existing = await db.TenantMemberships.SingleOrDefaultAsync(
                x => x.TenantId == tenant.TenantId && x.UserId == request.UserId, token);
            if (existing?.Status == "ACTIVE")
                return Results.Conflict(new { error = "tenant_membership_already_active" });

            if (existing is not null &&
                tenant.RoleCode.Equals("ADMIN", StringComparison.OrdinalIgnoreCase) &&
                existing.RoleCode is "OWNER" or "ADMIN")
            {
                return Results.Forbid();
            }

            var quotaResult = await CheckActiveUserQuotaAsync(db, tenant.TenantId, token);
            if (quotaResult is not null)
                return quotaResult;

            if (existing is null)
            {
                existing = new TenantMembershipRecord
                {
                    TenantId = tenant.TenantId,
                    UserId = request.UserId,
                    Status = "ACTIVE",
                    RoleCode = role,
                    CreatedAt = DateTime.UtcNow
                };
                db.TenantMemberships.Add(existing);
            }
            else
            {
                existing.Status = "ACTIVE";
                existing.RoleCode = role;
            }

            db.AuditLogs.Add(CreateAudit(tenant, http, "TENANT_MEMBERSHIP_ASSIGNED",
                request.UserId, new { roleCode = role }));
            await db.SaveChangesAsync(token);

            var dto = new TenantMemberDto(targetUser.Id, targetUser.DisplayName, targetUser.Email,
                existing.RoleCode, existing.Status, existing.CreatedAt, false);
            return Results.Created($"/api/v1/memberships/{request.UserId:D}", dto);
        }, ct);
    }

    private static async Task<IResult> UpdateMember(
        Guid userId,
        UpdateMembershipRequest request,
        ITenantContext tenant,
        HttpContext http,
        TenantTransactionRunner runner,
        CancellationToken ct)
    {
        if (!HasRole(tenant.RoleCode, ManagerRoles))
            return Results.Forbid();

        var role = NormalizeRole(request.RoleCode);
        var status = request.Status?.Trim().ToUpperInvariant();
        if (role is null)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["roleCode"] = ["الدور يجب أن يكون OWNER أو ADMIN أو ACCOUNTANT أو VIEWER."]
            });
        if (status is null || !AllowedStatuses.Contains(status))
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["status"] = ["الحالة يجب أن تكون ACTIVE أو SUSPENDED أو REMOVED."]
            });

        if (tenant.RoleCode.Equals("ADMIN", StringComparison.OrdinalIgnoreCase) &&
            role is "OWNER" or "ADMIN")
        {
            return Results.Forbid();
        }

        if (userId == Guid.Empty)
            return Results.BadRequest(new { error = "valid_user_id_required" });

        return await runner.ExecuteAsync<IResult>(async (db, token) =>
        {
            await AcquireTenantMembershipLockAsync(db, tenant.TenantId, token);

            var targetUser = await db.Users.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == userId, token);
            if (targetUser is null)
                return Results.NotFound(new { error = "provisioned_user_not_found" });
            if (status == "ACTIVE" && targetUser.DisabledAt is not null)
                return Results.Conflict(new { error = "identity_user_disabled" });

            var membership = await db.TenantMemberships.SingleOrDefaultAsync(
                x => x.TenantId == tenant.TenantId && x.UserId == userId, token);
            if (membership is null)
                return Results.NotFound(new { error = "tenant_membership_not_found" });

            if (tenant.RoleCode.Equals("ADMIN", StringComparison.OrdinalIgnoreCase) &&
                membership.RoleCode is "OWNER" or "ADMIN")
            {
                return Results.Forbid();
            }

            if (userId == tenant.UserId && status != "ACTIVE")
            {
                return Results.Conflict(new
                {
                    error = "cannot_suspend_self",
                    message = "لا يمكنك إيقاف عضويتك الحالية. اطلب من مالك آخر إدارة العضوية."
                });
            }

            if (membership.Status == "ACTIVE" && membership.RoleCode == "OWNER" &&
                (status != "ACTIVE" || role != "OWNER"))
            {
                var activeOwners = await db.TenantMemberships.CountAsync(
                    x => x.TenantId == tenant.TenantId &&
                         x.Status == "ACTIVE" &&
                         x.RoleCode == "OWNER", token);
                if (activeOwners <= 1)
                {
                    return Results.Conflict(new
                    {
                        error = "last_owner_protection",
                        message = "لا يمكن تعطيل آخر مالك نشط للمستأجر أو تغيير دوره قبل تعيين مالك بديل."
                    });
                }
            }

            if (membership.Status != "ACTIVE" && status == "ACTIVE")
            {
                var quotaResult = await CheckActiveUserQuotaAsync(db, tenant.TenantId, token);
                if (quotaResult is not null)
                    return quotaResult;
            }

            var oldRole = membership.RoleCode;
            var oldStatus = membership.Status;
            membership.RoleCode = role;
            membership.Status = status;

            db.AuditLogs.Add(CreateAudit(tenant, http, "TENANT_MEMBERSHIP_UPDATED", userId,
                new { oldRole, oldStatus, newRole = role, newStatus = status }));
            await db.SaveChangesAsync(token);

            return Results.Ok(new TenantMemberDto(targetUser.Id, targetUser.DisplayName, targetUser.Email,
                membership.RoleCode, membership.Status, membership.CreatedAt, targetUser.DisabledAt != null));
        }, ct);
    }

    private static async Task<IResult?> CheckActiveUserQuotaAsync(
        ErpDbContext db,
        Guid tenantId,
        CancellationToken ct)
    {
        var limitsJson = await (
            from subscription in db.TenantSubscriptions.AsNoTracking()
            join plan in db.SubscriptionPlans.AsNoTracking()
                on subscription.SubscriptionPlanId equals plan.Id
            where subscription.TenantId == tenantId
            orderby subscription.StartsAt descending, subscription.CreatedAt descending
            select plan.LimitsJson)
            .FirstOrDefaultAsync(ct);

        var limit = ReadPlanLimit(limitsJson, "users");
        if (limit is not int maxUsers)
            return null;

        var activeUsers = await db.TenantMemberships.CountAsync(
            x => x.TenantId == tenantId && x.Status == "ACTIVE", ct);
        if (activeUsers < maxUsers)
            return null;

        return Results.Conflict(new
        {
            error = "plan_limit_exceeded",
            limitName = "users",
            limit = maxUsers,
            current = activeUsers,
            message = $"وصل المستأجر إلى حد المستخدمين النشطين في خطته ({maxUsers}). رقِّ الخطة أو تواصل مع مشغل المنصة."
        });
    }

    private static int? ReadPlanLimit(string? limitsJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(limitsJson))
            throw new InvalidOperationException("The current subscription plan has no valid limits_json configuration.");

        using var document = JsonDocument.Parse(limitsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("The subscription plan limits_json must be a JSON object.");

        if (!document.RootElement.TryGetProperty(propertyName, out var element))
            return null;

        if (element.ValueKind != JsonValueKind.Number ||
            !element.TryGetInt32(out var limit) ||
            limit < 0)
        {
            throw new InvalidOperationException($"The subscription plan limit '{propertyName}' must be a non-negative integer.");
        }

        return limit;
    }

    private static string? NormalizeRole(string? value)
    {
        var role = value?.Trim().ToUpperInvariant();
        return role is not null && AllowedRoles.Contains(role) ? role : null;
    }

    private static Task<int> AcquireTenantMembershipLockAsync(
        ErpDbContext db, Guid tenantId, CancellationToken ct)
    {
        var lockKey = $"tenant-membership-quota:{tenantId:D}";
        return db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))",
            ct);
    }

    private static bool HasRole(string role, params string[] allowedRoles) =>
        allowedRoles.Contains(role, StringComparer.OrdinalIgnoreCase);

    private static AuditLogRecord CreateAudit(
        ITenantContext tenant,
        HttpContext http,
        string action,
        Guid userId,
        object details) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenant.TenantId,
        UserId = tenant.UserId,
        Action = action,
        EntityType = "TenantMembership",
        EntityId = userId.ToString("D"),
        IpAddress = http.Connection.RemoteIpAddress?.ToString(),
        DetailsJson = JsonSerializer.Serialize(details),
        CreatedAt = DateTime.UtcNow
    };

    public sealed record AssignMembershipRequest(Guid UserId, string RoleCode);
    public sealed record UpdateMembershipRequest(string RoleCode, string Status);
    public sealed record TenantMemberDto(
        Guid UserId,
        string DisplayName,
        string Email,
        string RoleCode,
        string Status,
        DateTime CreatedAt,
        bool IdentityDisabled);
}
