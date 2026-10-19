using System.Text.Json;
using BusinessERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BusinessERP.Infrastructure.Tests;

public sealed class SubscriptionPersistenceTests
{
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-4222-8222-222222222222");

    [RequiresPostgresFact]
    public async Task Subscription_mapping_respects_rls_and_reads_plan_limits()
    {
        var connectionString = Environment.GetEnvironmentVariable("BUSINESS_ERP_TEST_CONNECTION");
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseNpgsql(connectionString)
            .EnableDetailedErrors()
            .Options;

        await using var db = new ErpDbContext(options);

        // The runtime role is not the schema owner. Without an explicit transaction-local tenant
        // context, RLS must hide all tenant-owned subscription records.
        Assert.Empty(await db.TenantSubscriptions.AsNoTracking().ToListAsync());

        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {TenantA.ToString("D")}, true)");

        var subscription = await (
            from item in db.TenantSubscriptions.AsNoTracking()
            join plan in db.SubscriptionPlans.AsNoTracking()
                on item.SubscriptionPlanId equals plan.Id
            where item.TenantId == TenantA
            select new
            {
                item.Status,
                item.StartsAt,
                item.EndsAt,
                PlanCode = plan.Code,
                PlanCurrency = plan.Currency,
                PlanPrice = plan.Price,
                plan.LimitsJson
            }).SingleOrDefaultAsync();

        Assert.NotNull(subscription);
        Assert.Equal("ACTIVE", subscription.Status);
        Assert.Equal("CI-STARTER", subscription.PlanCode);
        Assert.Equal("SAR", subscription.PlanCurrency.Trim());
        Assert.Equal(0m, subscription.PlanPrice);
        using var limits = JsonDocument.Parse(subscription.LimitsJson);
        Assert.Equal(2, limits.RootElement.GetProperty("companies").GetInt32());
        Assert.Equal(3, limits.RootElement.GetProperty("users").GetInt32());

        // A known foreign tenant identifier is not sufficient to read its rows in this context.
        Assert.Empty(await db.TenantSubscriptions.AsNoTracking()
            .Where(x => x.TenantId == TenantB)
            .ToListAsync());

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {TenantB.ToString("D")}, true)");
        Assert.Empty(await db.TenantSubscriptions.AsNoTracking()
            .Where(x => x.TenantId == TenantA)
            .ToListAsync());

        await transaction.RollbackAsync();
    }
}

public sealed class RequiresPostgresFactAttribute : FactAttribute
{
    public RequiresPostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BUSINESS_ERP_TEST_CONNECTION")))
        {
            Skip = "Set BUSINESS_ERP_TEST_CONNECTION to a disposable PostgreSQL database with the CI fixtures applied.";
        }
    }
}
