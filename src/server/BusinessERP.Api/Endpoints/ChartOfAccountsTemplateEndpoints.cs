using BusinessERP.Application.Abstractions;
using BusinessERP.Infrastructure.Persistence;
using BusinessERP.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace BusinessERP.Api.Endpoints;

/// <summary>
/// Applies a reviewed database reference template to an empty company chart. Templates are
/// not accounting policy and must be reviewed by the customer's accountant before posting.
/// </summary>
public static class ChartOfAccountsTemplateEndpoints
{
    private static readonly string[] CanApplyTemplateRoles = ["OWNER", "ADMIN"];

    public static IEndpointRouteBuilder MapChartOfAccountsTemplateEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1").WithTags("Chart of accounts templates");
        api.MapGet("/account-templates", GetTemplates).WithName("GetAccountTemplates");
        api.MapPost("/companies/{companyId:guid}/accounts/apply-template", ApplyTemplate).WithName("ApplyAccountTemplate");
        return endpoints;
    }

    private static async Task<IResult> GetTemplates(TenantTransactionRunner runner, CancellationToken ct) =>
        await runner.ExecuteAsync<IResult>(async (db, token) =>
        {
            var templates = await db.AccountTemplates.AsNoTracking()
                .Where(x => x.IsActive)
                .OrderBy(x => x.NameAr)
                .Select(x => new AccountTemplateDto(x.Code, x.IndustryCode, x.NameAr, x.NameEn, x.DescriptionAr,
                    db.AccountTemplateAccounts.Count(a => a.TemplateId == x.Id)))
                .ToListAsync(token);
            return Results.Ok(templates);
        }, ct);

    private static async Task<IResult> ApplyTemplate(
        Guid companyId,
        ApplyAccountTemplateRequest request,
        ITenantContext tenant,
        HttpContext http,
        TenantTransactionRunner runner,
        CancellationToken ct)
    {
        if (!CanApplyTemplateRoles.Contains(tenant.RoleCode, StringComparer.OrdinalIgnoreCase))
            return Results.Forbid();

        var code = request.TemplateCode?.Trim().ToUpperInvariant() ?? "";
        if (code.Length is < 1 or > 64)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["templateCode"] = ["رمز القالب مطلوب."] });

        return await runner.ExecuteAsync<IResult>(async (db, token) =>
        {
            var company = await db.Companies.SingleOrDefaultAsync(x => x.Id == companyId, token);
            if (company is null) return Results.NotFound();

            if (await db.Accounts.AnyAsync(x => x.CompanyId == companyId, token))
                return Results.Conflict(new
                {
                    error = "chart_of_accounts_not_empty",
                    message = "لا يمكن تطبيق قالب تلقائي على شركة لديها حسابات. احذف القالب من قائمة الاختيارات أو أنشئ الشركة من جديد؛ لن تُحذف بيانات قائمة."
                });

            var template = await db.AccountTemplates.AsNoTracking()
                .Include(x => x.Accounts)
                .SingleOrDefaultAsync(x => x.Code == code && x.IsActive, token);
            if (template is null) return Results.NotFound(new { error = "account_template_not_found" });
            if (template.Accounts.Count == 0)
                return Results.Problem(statusCode: StatusCodes.Status500InternalServerError,
                    title: "قالب دليل الحسابات فارغ", detail: "يجب إصلاح بيانات القالب في قاعدة البيانات قبل تطبيقه.");

            var accountIdsByCode = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            var newAccounts = new List<AccountRecord>(template.Accounts.Count);
            foreach (var item in template.Accounts.OrderBy(x => x.SortOrder).ThenBy(x => x.Code, StringComparer.Ordinal))
            {
                Guid? parentId = null;
                if (!string.IsNullOrWhiteSpace(item.ParentCode))
                {
                    if (!accountIdsByCode.TryGetValue(item.ParentCode, out var resolvedParentId))
                        return Results.Problem(statusCode: StatusCodes.Status500InternalServerError,
                            title: "قالب دليل الحسابات غير صالح",
                            detail: $"الحساب الأب {item.ParentCode} مفقود أو يأتي بعد الحساب {item.Code}.");
                    parentId = resolvedParentId;
                }

                var account = new AccountRecord
                {
                    Id = Guid.NewGuid(), TenantId = tenant.TenantId, CompanyId = companyId,
                    ParentAccountId = parentId, Code = item.Code,
                    NameAr = item.NameAr, NameEn = item.NameEn,
                    AccountType = item.AccountType, NormalBalance = item.NormalBalance,
                    IsActive = true, AllowPosting = item.AllowPosting, CreatedAt = DateTime.UtcNow
                };
                accountIdsByCode.Add(account.Code, account.Id);
                newAccounts.Add(account);
            }

            db.Accounts.AddRange(newAccounts);
            db.AuditLogs.Add(new AuditLogRecord
            {
                Id = Guid.NewGuid(), TenantId = tenant.TenantId, UserId = tenant.UserId,
                Action = "ACCOUNT_TEMPLATE_APPLIED", EntityType = "Company", EntityId = companyId.ToString("D"),
                IpAddress = http.Connection.RemoteIpAddress?.ToString(), CreatedAt = DateTime.UtcNow,
                DetailsJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    template.Code, template.IndustryCode, TemplateName = template.NameEn,
                    AccountsCreated = newAccounts.Count
                })
            });
            await db.SaveChangesAsync(token);
            return Results.Ok(new ApplyAccountTemplateResult(
                companyId, template.Code, template.NameAr, newAccounts.Count,
                newAccounts.Count(x => x.AllowPosting),
                "تم تطبيق القالب. راجع الحسابات وسياسة الضرائب وصلاحيات الترحيل مع محاسب مختص قبل إدخال عمليات حقيقية."));
        }, ct);
    }

    public sealed record ApplyAccountTemplateRequest(string TemplateCode);
    public sealed record AccountTemplateDto(string Code, string IndustryCode, string NameAr, string NameEn, string DescriptionAr, int AccountCount);
    public sealed record ApplyAccountTemplateResult(Guid CompanyId, string TemplateCode, string TemplateNameAr, int AccountsCreated, int PostingAccounts, string Warning);
}
