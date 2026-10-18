using System.Text.Json;
using BusinessERP.Application.Abstractions;
using BusinessERP.Infrastructure.Persistence;
using BusinessERP.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace BusinessERP.Api.Endpoints;

/// <summary>
/// Read-only tenant-facing subscription visibility. Plans and subscription rows are provisioned
/// by a trusted platform operator until a reviewed billing provider and checkout workflow exist.
/// </summary>
public static class SubscriptionEndpoints
{
    public static IEndpointRouteBuilder MapSubscriptionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1").WithTags("SaaS subscriptions");
        api.MapGet("/subscription-plans", GetActivePlans).WithName("GetActiveSubscriptionPlans");
        api.MapGet("/subscription", GetCurrentSubscription).WithName("GetCurrentTenantSubscription");
        return endpoints;
    }

    private static async Task<IResult> GetActivePlans(TenantTransactionRunner runner, CancellationToken ct) =>
        await runner.ExecuteAsync<IResult>(async (db, token) =>
        {
            var plans = await db.SubscriptionPlans.AsNoTracking()
                .Where(x => x.IsActive)
                .OrderBy(x => x.Price)
                .ThenBy(x => x.Name)
                .Select(x => new
                {
                    x.Code, x.Name, x.BillingInterval, x.Price, x.Currency, x.LimitsJson
                })
                .ToListAsync(token);

            return Results.Ok(plans.Select(x => new SubscriptionPlanDto(
                x.Code, x.Name, x.BillingInterval, x.Price, x.Currency.Trim(), ParseLimits(x.LimitsJson))).ToList());
        }, ct);

    private static async Task<IResult> GetCurrentSubscription(
        ITenantContext tenant,
        TenantTransactionRunner runner,
        CancellationToken ct) =>
        await runner.ExecuteAsync<IResult>(async (db, token) =>
        {
            var subscription = await (
                from s in db.TenantSubscriptions.AsNoTracking()
                join p in db.SubscriptionPlans.AsNoTracking() on s.SubscriptionPlanId equals p.Id
                where s.TenantId == tenant.TenantId
                orderby s.StartsAt descending, s.CreatedAt descending
                select new
                {
                    s.Status,
                    s.StartsAt,
                    s.EndsAt,
                    p.Code,
                    p.Name,
                    p.BillingInterval,
                    p.Price,
                    p.Currency,
                    p.LimitsJson
                }).FirstOrDefaultAsync(token);

            if (subscription is null)
            {
                return Results.Ok(new TenantSubscriptionStatusDto(
                    false, false, null, null, null, null,
                    "لا يوجد اشتراك مخصص لهذا العميل بعد. يجب أن يخصص مشغل المنصة خطة عبر إجراء إداري موثوق."));
            }

            var now = DateTime.UtcNow;
            var subscriptionWindowOpen = subscription.StartsAt <= now &&
                (!subscription.EndsAt.HasValue || subscription.EndsAt.Value > now);
            var entitled = subscriptionWindowOpen &&
                (subscription.Status.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase) ||
                 subscription.Status.Equals("TRIALING", StringComparison.OrdinalIgnoreCase));

            return Results.Ok(new TenantSubscriptionStatusDto(
                true,
                entitled,
                subscription.Status,
                subscription.StartsAt,
                subscription.EndsAt,
                new SubscriptionPlanDto(
                    subscription.Code,
                    subscription.Name,
                    subscription.BillingInterval,
                    subscription.Price,
                    subscription.Currency.Trim(),
                    ParseLimits(subscription.LimitsJson)),
                entitled
                    ? "الاشتراك ضمن فترة سارية. هذه الحالة للعرض فقط ولا تمثل تحصيلًا أو تأكيدًا من مزود دفع."
                    : "الاشتراك غير مستحق للاستخدام وفق حالته أو نافذته الزمنية. لم يُربط حتى الآن نظام فوترة أو سياسة مهلة سداد."));
        }, ct);

    private static JsonElement ParseLimits(string json)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        return document.RootElement.Clone();
    }

    public sealed record SubscriptionPlanDto(
        string Code,
        string Name,
        string BillingInterval,
        decimal Price,
        string Currency,
        JsonElement Limits);

    public sealed record TenantSubscriptionStatusDto(
        bool HasSubscription,
        bool IsEntitled,
        string? Status,
        DateTime? StartsAt,
        DateTime? EndsAt,
        SubscriptionPlanDto? Plan,
        string Message);
}
