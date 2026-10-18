using System.Text.Json;
using BusinessERP.Application.Abstractions;
using BusinessERP.Infrastructure.Persistence;
using BusinessERP.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace BusinessERP.Api.Endpoints;

/// <summary>
/// Creates and posts a balanced internal journal entry for a VAT-calculated sales draft.
/// This is a GL accounting action only; it does not issue a legal tax invoice or submit anything to ZATCA.
/// </summary>
public static class SalesDraftLedgerPostingEndpoints
{
    private static readonly string[] AllowedRoles = ["OWNER", "ADMIN", "ACCOUNTANT"];

    public static IEndpointRouteBuilder MapSalesDraftLedgerPostingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
                "/api/v1/companies/{companyId:guid}/sales-invoice-drafts/{invoiceId:guid}/post-to-ledger",
                PostDraftToLedger)
            .WithName("PostSalesDraftToLedger")
            .WithTags("Sales - Accounting posting");
        return endpoints;
    }

    private static async Task<IResult> PostDraftToLedger(
        Guid companyId, Guid invoiceId, PostSalesDraftToLedgerRequest request,
        ITenantContext tenant, HttpContext http, TenantTransactionRunner runner, CancellationToken ct)
    {
        if (!AllowedRoles.Contains(tenant.RoleCode, StringComparer.OrdinalIgnoreCase))
            return Results.Forbid();

        var entryNumber = (request.EntryNumber ?? "").Trim().ToUpperInvariant();
        if (entryNumber.Length is < 1 or > 80 ||
            entryNumber.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '/' or '.')))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["entryNumber"] = ["رقم القيد مطلوب ويقبل الأحرف اللاتينية والأرقام والشرطة والنقطة والشرطة السفلية والشرطة المائلة."]
            });
        }
        if (request.FiscalPeriodId == Guid.Empty ||
            request.ReceivableAccountId == Guid.Empty || request.RevenueAccountId == Guid.Empty)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["accounts"] = ["الفترة والحسابات المطلوبة للقيد إلزامية."]
            });
        }

        return await runner.ExecuteAsync<IResult>(async (db, token) =>
        {
            var company = await db.Companies.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == companyId && x.TenantId == tenant.TenantId, token);
            if (company is null)
                return Results.NotFound(new { error = "company_not_found" });

            var draftLock = $"sales-draft-ledger-posting:{tenant.TenantId:D}:{companyId:D}:{invoiceId:D}";
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({draftLock}, 0))", token);

            var draft = await db.Database.SqlQuery<SalesDraftPostingSource>($"""
                SELECT id AS "Id",
                       status AS "Status",
                       tax_calculation_status AS "TaxCalculationStatus",
                       issue_date AS "IssueDate",
                       currency_code AS "CurrencyCode",
                       subtotal_amount AS "SubtotalAmount",
                       gross_amount AS "GrossAmount",
                       discount_amount AS "DiscountAmount",
                       net_amount AS "NetAmount",
                       vat_amount AS "VatAmount",
                       total_amount AS "TotalAmount",
                       accounting_posted_journal_entry_id AS "AccountingPostedJournalEntryId",
                       accounting_posted_at AS "AccountingPostedAt"
                  FROM sales_invoice_drafts
                 WHERE tenant_id = {tenant.TenantId}
                   AND company_id = {companyId}
                   AND id = {invoiceId}
                 LIMIT 1
                """).SingleOrDefaultAsync(token);

            if (draft is null)
                return Results.NotFound(new { error = "sales_invoice_draft_not_found" });

            if (draft.Status == "ACCOUNTING_POSTED")
            {
                if (!draft.AccountingPostedJournalEntryId.HasValue)
                    return Results.Problem(statusCode: StatusCodes.Status500InternalServerError,
                        title: "Sales draft accounting link is incomplete");

                var existing = await db.JournalEntries.AsNoTracking().SingleOrDefaultAsync(
                    x => x.Id == draft.AccountingPostedJournalEntryId.Value &&
                         x.CompanyId == companyId &&
                         x.SourceType == "SALES_INVOICE_DRAFT" && x.SourceId == invoiceId &&
                         x.Status == "POSTED", token);

                if (existing is null)
                    return Results.Problem(statusCode: StatusCodes.Status500InternalServerError,
                        title: "Posted journal entry for this sales draft was not found");

                return Results.Ok(ToResult(draft, existing, alreadyPosted: true));
            }

            if (draft.Status != "DRAFT")
                return Results.Conflict(new { error = "sales_invoice_draft_not_postable", status = draft.Status });

            if (draft.TaxCalculationStatus != "CALCULATED" ||
                !draft.GrossAmount.HasValue || !draft.DiscountAmount.HasValue ||
                !draft.NetAmount.HasValue || !draft.VatAmount.HasValue || !draft.TotalAmount.HasValue)
                return Results.Conflict(new
                {
                    error = "sales_draft_vat_not_calculated",
                    message = "احسب واحفظ تصنيف VAT على كل بنود المسودة قبل ترحيلها إلى دفتر الأستاذ."
                });

            var gross = draft.GrossAmount.Value;
            var discount = draft.DiscountAmount.Value;
            var net = draft.NetAmount.Value;
            var vat = draft.VatAmount.Value;
            var total = draft.TotalAmount.Value;
            if (total <= 0m || gross - discount != net || net + vat != total)
                return Results.Conflict(new { error = "sales_draft_tax_totals_invalid" });

            if (!string.Equals(company.BaseCurrency.Trim(), draft.CurrencyCode.Trim(), StringComparison.OrdinalIgnoreCase))
                return Results.Conflict(new { error = "sales_draft_currency_mismatch" });

            var period = await db.FiscalPeriods.AsNoTracking().SingleOrDefaultAsync(
                x => x.Id == request.FiscalPeriodId && x.CompanyId == companyId, token);
            if (period is null)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["fiscalPeriodId"] = ["الفترة المالية غير موجودة ضمن الشركة."]
                });
            if (period.Status != "OPEN" || draft.IssueDate < period.StartDate || draft.IssueDate > period.EndDate)
                return Results.Conflict(new { error = "fiscal_period_not_open_for_sales_draft_date" });

            if (request.ReceivableAccountId == request.RevenueAccountId ||
                (vat > 0m && (!request.OutputVatAccountId.HasValue ||
                              request.OutputVatAccountId.Value == request.ReceivableAccountId ||
                              request.OutputVatAccountId.Value == request.RevenueAccountId)))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["accounts"] = ["يجب اختيار حسابات منفصلة للذمم المدينة والإيراد وضريبة المخرجات."]
                });
            }

            var requiredAccountIds = new List<Guid>
            {
                request.ReceivableAccountId,
                request.RevenueAccountId
            };
            if (vat > 0m)
                requiredAccountIds.Add(request.OutputVatAccountId!.Value);

            var accounts = await db.Accounts.AsNoTracking()
                .Where(x => x.CompanyId == companyId && requiredAccountIds.Contains(x.Id))
                .ToListAsync(token);
            if (accounts.Count != requiredAccountIds.Distinct().Count())
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["accounts"] = ["أحد الحسابات غير موجود ضمن الشركة الحالية."]
                });

            var receivable = accounts.Single(x => x.Id == request.ReceivableAccountId);
            var revenue = accounts.Single(x => x.Id == request.RevenueAccountId);
            if (!IsPostable(receivable, "ASSET", "DEBIT") || !IsPostable(revenue, "REVENUE", "CREDIT"))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["accounts"] = ["حساب الذمم يجب أن يكون أصلًا مدينًا، وحساب الإيراد يجب أن يكون إيرادًا دائنًا ونشطًا وقابلًا للترحيل."]
                });

            AccountRecord? vatAccount = null;
            if (vat > 0m)
            {
                vatAccount = accounts.Single(x => x.Id == request.OutputVatAccountId!.Value);
                if (!IsPostable(vatAccount, "LIABILITY", "CREDIT"))
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["outputVatAccountId"] = ["حساب ضريبة المخرجات يجب أن يكون التزامًا دائنًا ونشطًا وقابلًا للترحيل."]
                    });
            }

            var entryNumberLock = $"journal-entry-number:{tenant.TenantId:D}:{companyId:D}:{entryNumber}";
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({entryNumberLock}, 0))", token);

            if (await db.JournalEntries.AnyAsync(
                    x => x.CompanyId == companyId && x.EntryNumber == entryNumber, token))
                return Results.Conflict(new { error = "journal_entry_number_conflict" });

            if (await db.JournalEntries.AnyAsync(
                    x => x.CompanyId == companyId &&
                         x.SourceType == "SALES_INVOICE_DRAFT" && x.SourceId == invoiceId, token))
                return Results.Conflict(new { error = "sales_draft_already_has_journal" });

            var postedAt = DateTime.UtcNow;
            var entry = new JournalEntryRecord
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.TenantId,
                CompanyId = companyId,
                FiscalPeriodId = period.Id,
                EntryNumber = entryNumber,
                EntryDate = draft.IssueDate,
                Description = $"Sales draft {invoiceId:D}",
                CurrencyCode = company.BaseCurrency.Trim().ToUpperInvariant(),
                Status = "DRAFT",
                SourceType = "SALES_INVOICE_DRAFT",
                SourceId = invoiceId,
                CreatedAt = postedAt
            };
            db.JournalEntries.Add(entry);
            db.JournalEntryLines.Add(new JournalEntryLineRecord
            {
                Id = Guid.NewGuid(), TenantId = tenant.TenantId, CompanyId = companyId,
                JournalEntryId = entry.Id, AccountId = receivable.Id, LineNumber = 1,
                Description = "Accounts receivable", Debit = total, Credit = 0m
            });
            db.JournalEntryLines.Add(new JournalEntryLineRecord
            {
                Id = Guid.NewGuid(), TenantId = tenant.TenantId, CompanyId = companyId,
                JournalEntryId = entry.Id, AccountId = revenue.Id, LineNumber = 2,
                Description = "Sales revenue", Debit = 0m, Credit = net
            });
            if (vat > 0m && vatAccount is not null)
            {
                db.JournalEntryLines.Add(new JournalEntryLineRecord
                {
                    Id = Guid.NewGuid(), TenantId = tenant.TenantId, CompanyId = companyId,
                    JournalEntryId = entry.Id, AccountId = vatAccount.Id, LineNumber = 3,
                    Description = "Output VAT payable", Debit = 0m, Credit = vat
                });
            }

            await db.SaveChangesAsync(token);

            entry.Status = "APPROVED";
            await db.SaveChangesAsync(token);

            entry.Status = "POSTED";
            entry.PostedAt = postedAt;
            await db.SaveChangesAsync(token);

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE sales_invoice_drafts
                   SET status = 'ACCOUNTING_POSTED',
                       accounting_posted_journal_entry_id = {entry.Id},
                       accounting_posted_at = {postedAt}
                 WHERE tenant_id = {tenant.TenantId}
                   AND company_id = {companyId}
                   AND id = {invoiceId}
                """, token);

            db.AuditLogs.Add(new AuditLogRecord
            {
                Id = Guid.NewGuid(), TenantId = tenant.TenantId, UserId = tenant.UserId,
                Action = "SALES_DRAFT_POSTED_TO_GL", EntityType = "SalesInvoiceDraft",
                EntityId = invoiceId.ToString("D"),
                IpAddress = http.Connection.RemoteIpAddress?.ToString(),
                DetailsJson = JsonSerializer.Serialize(new
                {
                    JournalEntryId = entry.Id,
                    EntryNumber = entry.EntryNumber,
                    ReceivableAccountId = receivable.Id,
                    RevenueAccountId = revenue.Id,
                    OutputVatAccountId = vatAccount?.Id,
                    GrossAmount = gross,
                    DiscountAmount = discount,
                    NetAmount = net,
                    VatAmount = vat,
                    TotalAmount = total
                }),
                CreatedAt = postedAt
            });
            await db.SaveChangesAsync(token);

            return Results.Created(
                $"/api/v1/journal-entries/{entry.Id}",
                ToResult(draft, entry, alreadyPosted: false));
        }, ct);
    }

    private static bool IsPostable(AccountRecord account, string type, string normalBalance) =>
        account.AccountType == type && account.NormalBalance == normalBalance &&
        account.IsActive && account.AllowPosting;

    private static SalesDraftLedgerPostingResult ToResult(
        SalesDraftPostingSource draft, JournalEntryRecord entry, bool alreadyPosted) =>
        new(draft.Id, entry.Id, entry.EntryNumber, entry.Status, entry.EntryDate,
            draft.GrossAmount ?? 0m, draft.DiscountAmount ?? 0m, draft.NetAmount ?? 0m,
            draft.VatAmount ?? 0m, draft.TotalAmount ?? 0m, alreadyPosted,
            "تم ترحيل الأثر المحاسبي الداخلي فقط؛ المستند ليس فاتورة ضريبية صادرة ولم يتم إرساله إلى ZATCA.");

    public sealed record PostSalesDraftToLedgerRequest(
        Guid FiscalPeriodId, string? EntryNumber, Guid ReceivableAccountId,
        Guid RevenueAccountId, Guid? OutputVatAccountId = null);

    public sealed record SalesDraftLedgerPostingResult(
        Guid SalesInvoiceDraftId, Guid JournalEntryId, string EntryNumber,
        string JournalEntryStatus, DateOnly EntryDate,
        decimal GrossAmount, decimal DiscountAmount, decimal NetAmount, decimal VatAmount, decimal TotalAmount,
        bool AlreadyPosted, string Message);

    public sealed class SalesDraftPostingSource
    {
        public Guid Id { get; init; }
        public string Status { get; init; } = "DRAFT";
        public string TaxCalculationStatus { get; init; } = "NOT_CALCULATED";
        public DateOnly IssueDate { get; init; }
        public string CurrencyCode { get; init; } = "SAR";
        public decimal SubtotalAmount { get; init; }
        public decimal? GrossAmount { get; init; }
        public decimal? DiscountAmount { get; init; }
        public decimal? NetAmount { get; init; }
        public decimal? VatAmount { get; init; }
        public decimal? TotalAmount { get; init; }
        public Guid? AccountingPostedJournalEntryId { get; init; }
        public DateTime? AccountingPostedAt { get; init; }
    }
}
