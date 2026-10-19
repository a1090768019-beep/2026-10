using Microsoft.AspNetCore.Diagnostics;
using Npgsql;

namespace BusinessERP.Api.Security;

/// <summary>
/// Converts the deliberately-raised PostgreSQL subscription guard errors to stable API responses.
/// The database trigger is the enforcement point so all journal-posting flows share the same quota.
/// </summary>
public sealed class SubscriptionQuotaExceptionHandler(ILogger<SubscriptionQuotaExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (httpContext.Response.HasStarted ||
            exception is not PostgresException postgres ||
            postgres.SqlState != "P0001")
        {
            return false;
        }

        if (string.Equals(postgres.MessageText, "subscription_quota_exceeded:entriesPerMonth", StringComparison.Ordinal))
        {
            logger.LogInformation("Blocked journal entry insertion because the tenant monthly subscription quota was reached.");
            httpContext.Response.StatusCode = StatusCodes.Status409Conflict;
            await httpContext.Response.WriteAsJsonAsync(new
            {
                error = "plan_limit_exceeded",
                limitName = "entriesPerMonth",
                message = "وصلت إلى الحد الشهري للقيود المحاسبية في خطتك. رقِّ الخطة أو تواصل مع مشغل المنصة."
            }, cancellationToken);
            return true;
        }

        if (string.Equals(postgres.MessageText, "subscription_required_or_inactive", StringComparison.Ordinal))
        {
            logger.LogInformation("Blocked journal entry insertion because the tenant has no active subscription.");
            httpContext.Response.StatusCode = StatusCodes.Status402PaymentRequired;
            await httpContext.Response.WriteAsJsonAsync(new
            {
                error = "subscription_required_or_inactive",
                message = "الاشتراك غير نشط أو انتهت مدته. راجع حالة الاشتراك أو تواصل مع مشغل المنصة."
            }, cancellationToken);
            return true;
        }

        return false;
    }
}
