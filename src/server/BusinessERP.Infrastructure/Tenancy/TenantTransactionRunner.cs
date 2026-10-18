using System.Data;
using BusinessERP.Application.Abstractions;
using BusinessERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace BusinessERP.Infrastructure.Tenancy;

/// <summary>
/// Sets PostgreSQL's transaction-local RLS variable and executes work in that same transaction.
/// Register/use only after trusted authentication AND active tenant membership have been validated.
/// This class deliberately does not validate identity or membership itself.
/// </summary>
public sealed class TenantTransactionRunner(ErpDbContext db, ITenantContext tenantContext)
{
    public async Task<TResult> ExecuteAsync<TResult>(
        Func<ErpDbContext, CancellationToken, Task<TResult>> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (tenantContext.TenantId == Guid.Empty || tenantContext.UserId == Guid.Empty)
            throw new InvalidOperationException("A trusted tenant and user context is required.");

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var connection = db.Database.GetDbConnection();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction.GetDbTransaction();
            command.CommandText = "SELECT set_config('app.tenant_id', @tenant_id, true)";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "tenant_id";
            parameter.Value = tenantContext.TenantId.ToString("D");
            command.Parameters.Add(parameter);
            await command.ExecuteScalarAsync(cancellationToken);
        }

        var result = await work(db, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}
