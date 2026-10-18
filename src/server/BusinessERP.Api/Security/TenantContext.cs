using BusinessERP.Application.Abstractions;

namespace BusinessERP.Api.Security;

public sealed class TenantContext : ITenantContext
{
    public Guid TenantId { get; private set; }
    public Guid UserId { get; private set; }
    public string RoleCode { get; private set; } = "";

    public void Set(Guid tenantId, Guid userId, string roleCode)
    {
        if (tenantId == Guid.Empty || userId == Guid.Empty)
            throw new ArgumentException("Tenant and user identifiers must be valid.");
        TenantId = tenantId;
        UserId = userId;
        RoleCode = roleCode;
    }
}
