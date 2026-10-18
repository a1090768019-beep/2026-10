namespace BusinessERP.Application.Abstractions;

/// <summary>Trusted server-side context resolved from a validated identity and active membership.</summary>
public interface ITenantContext
{
    Guid TenantId { get; }
    Guid UserId { get; }
    string RoleCode { get; }
}
