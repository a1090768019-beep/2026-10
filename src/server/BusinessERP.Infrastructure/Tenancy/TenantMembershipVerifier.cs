using BusinessERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace BusinessERP.Infrastructure.Tenancy;

/// <summary>
/// Verifies membership for an authenticated internal user and an untrusted candidate tenant ID.
/// The candidate ID is used only to scope this one RLS-protected membership query; it grants no
/// access by itself. Business data must only be read after this method returns true.
/// </summary>
public sealed class TenantMembershipVerifier(ErpDbContext db)
{
    public async Task<bool> IsActiveMemberAsync(
        Guid candidateTenantId,
        Guid authenticatedUserId,
        CancellationToken cancellationToken = default) =>
        (await GetActiveRoleAsync(candidateTenantId, authenticatedUserId, cancellationToken)) is not null;

    public async Task<string?> GetActiveRoleAsync(
        Guid candidateTenantId,
        Guid authenticatedUserId,
        CancellationToken cancellationToken = default)
    {
        if (candidateTenantId == Guid.Empty || authenticatedUserId == Guid.Empty)
            return null;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction.GetDbTransaction();
            command.CommandText = "SELECT set_config('app.tenant_id', @tenant_id, true)";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "tenant_id";
            parameter.Value = candidateTenantId.ToString("D");
            command.Parameters.Add(parameter);
            await command.ExecuteScalarAsync(cancellationToken);
        }

        // authenticatedUserId originates from the validated local-user mapping, never a request body.
        var roleCode = await db.TenantMemberships.AsNoTracking()
            .Where(membership => membership.TenantId == candidateTenantId &&
                                 membership.UserId == authenticatedUserId &&
                                 membership.Status == "ACTIVE")
            .Select(membership => membership.RoleCode)
            .SingleOrDefaultAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return roleCode;
    }
}
