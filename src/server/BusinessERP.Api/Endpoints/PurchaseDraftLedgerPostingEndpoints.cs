using System.Text.Json;
using BusinessERP.Application.Abstractions;
using BusinessERP.Infrastructure.Persistence;
using BusinessERP.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace BusinessERP.Api.Endpoints;

/// <summary>
/// Posts a VAT-calculated internal purchase draft to the double-entry ledger.
/// It does not issue a legal tax invoice. Input VAT is posted to an asset only when the user
/// explicitly selects an input-VAT account; otherwise tax remains part of the purchase debit.
/// </summary>
public static class PurchaseDraftLedgerPostingEndpoints
{
    private static readonly string[] AllowedRoles = ["OWNER", "ADMIN", "ACCOUNTANT"];

    public static IEndpointRouteBuilder MapPurchaseDraftLedgerPostingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
                "/api/v1/companies/{companyId:guid}/purchase-invoice-drafts/{invoiceId:guid}/post-to-ledger",
                PostDraftToLedger)
            .WithName("PostPurchaseDraftToLedger")
            .WithTags("Purchases - Accounting posting");
        return endpoints;
    }

    private static async Task<IResult> PostDraftToLedger(
        Guid companyId, Guid invoiceId, PostPurchaseDraftToLedgerRequest request,
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
            request.PurchaseDebitAccountId == Guid.Empty || request.PayableAccountId == Guid.Empty)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["accounts"] = ["الفترة المالية وحساب تكلفة الشراء وحساب الموردين مطلوبة."]
            });
        }

        return await runner.ExecuteAsync<IResult>(async (db, token) =>
        {
            var company = await db.Companies.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == companyId && x.TenantId == tenant.TenantId, token);
            if (company is null)
                return Results.NotFound(new { error = "company_not_found" });

            var draftLock = $"purchase-draft-ledger-posting:{tenant.TenantId:D}:{companyId:D}:{invoiceId:D}";
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({draftLock}, 0))", token);

            var draft = await db.Database.SqlQuery<PurchaseDraftPostingSource>($"""
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
                       accounting_posted_at AS "AccountingPostedAt",
                       accounting_input_vat_account_id AS "AccountingInputVatAccountId"
                  FROM purchase_invoice_drafts
                 WHERE tenant_id = {tenant.TenantId}
                   AND company_id = {companyId}
                   AND id = {invoiceId}
                 LIMIT 1
                """).SingleOrDefaultAsync(token);

            if (draft is null)
                return Results.NotFound(new { error = "purchase_invoice_draft_not_found" });

            if (draft.Status == "ACCOUNTING_POSTED")
            {
                if (!draft.AccountingPostedJournalEntryId.HasValue)
                    return Results.Problem(statusCode: StatusCodes.Status500InternalServerError,
                        title: "Purchase draft accounting link is incomplete");

                var existing = await db.JournalEntries.AsNoTracking().SingleOrDefaultAsync(
                    x => x.Id == draft.AccountingPostedJournalEntryId.Value &&
                         x.TenantId == tenant.TenantId &&
                         x.CompanyId == companyId &&
                         x.SourceType == "PURCHASE_INVOICE_DRAFT" && x.SourceId == invoiceId &&
                         x.Status == "POSTED", token);

                if (existing is null)
                    return Results.Problem(statusCode: StatusCodes.Status500InternalServerError,
                        title: "Posted journal entry for this purchase draft was not found");

                return Results.Ok(ToResult(draft, existing, alreadyPosted: true));
            }

            if (draft.Status != "DRAFT")
                return Results.Conflict(new { error = "purchase_invoice_draft_not_postable", status = draft.Status });

            if (draft.TaxCalculationStatus != "CALCULATED" ||
                !draft.GrossAmount.HasValue || !draft.DiscountAmount.HasValue ||
                !draft.NetAmount.HasValue || !draft.VatAmount.HasValue || !draft.TotalAmount.HasValue)
                return Results.Conflict(new
                {
                    error = "purchase_draft_vat_not_calculated",
                    message = "احسب واحفظ تصنيف VAT على بنود المسودة قبل الترحيل إلى دفتر الأستاذ."
                });

            var gross = draft.GrossAmount.Value;
            var discount = draft.DiscountAmount.Value;
            var net = draft.NetAmount.Value;
            var vat = draft.VatAmount.Value;
            var total = draft.TotalAmount.Value;
            if (total <= 0m || gross - discount != net || net + vat != total)
                return Results.Conflict(new { error = "purchase_draft_tax_totals_invalid" });

            if (!string.Equals(company.BaseCurrency.Trim(), draft.CurrencyCode.Trim(), StringComparison.OrdinalIgnoreCase))
                return Results.Conflict(new { error = "purchase_draft_currency_mismatch" });

            var period = await db.FiscalPeriods.AsNoTracking().SingleOrDefaultAsync(
                x => x.Id == request.FiscalPeriodId && x.TenantId == tenant.TenantId && x.CompanyId == companyId, token);
            if (period is null)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["fiscalPeriodId"] = ["الفترة المالية غير موجودة ضمن الشركة."]
                });
            if (period.Status != "OPEN" || draft.IssueDate < period.StartDate || draft.IssueDate > period.EndDate)
                return Results.Conflict(new { error = "fiscal_period_not_open_for_purchase_draft_date" });

            var postVatAsAsset = vat > 0m && request.InputVatAccountId.HasValue;
            var purchaseDebitAmount = postVatAsAsset ? net : total;
            if (request.PurchaseDebitAccountId == request.PayableAccountId ||
                (postVatAsAsset &&
                 (request.InputVatAccountId!.Value == request.PurchaseDebitAccountId ||
                  request.InputVatAccountId.Value == request.PayableAccountId)))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["accounts"] = ["يجب اختيار حسابات منفصلة لتكلفة الشراء والموردين وضريبة المدخلات."]
                });
            }

            var requiredAccountIds = new List<Guid>
            {
                request.PurchaseDebitAccountId,
                request.PayableAccountId
            };
            if (postVatAsAsset)
                requiredAccountIds.Add(request.InputVatAccountId!.Value);

            var accounts = await db.Accounts.AsNoTracking()
                .Where(x => x.TenantId == tenant.TenantId && x.CompanyId == companyId &&
                            requiredAccountIds.Contains(x.Id))
                .ToListAsync(token);
            if (accounts.Count != requiredAccountIds.Distinct().Count())
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["accounts"] = ["أحد الحسابات غير موجود ضمن الشركة الحالية."]
                });

            var purchaseDebit = accounts.Single(x => x.Id == request.PurchaseDebitAccountId);
            var payable = accounts.Single(x => x.Id == request.PayableAccountId);
            if (!IsPurchaseDebitPostable(purchaseDebit) || !IsPostable(payable, "LIABILITY", "CREDIT"))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["accounts"] = ["حساب تكلفة الشراء يجب أن يكون أصلًا أو مصروفًا مدينًا، وحساب الموردين يجب أن يكون التزامًا دائنًا ونشطًا وقابلًا للترحيل."]
                });

            AccountRecord? inputVatAccount = null;
            if (postVatAsAsset)
            {
                inputVatAccount = accounts.Single(x => x.Id == request.InputVatAccountId!.Value);
                if (!IsPostable(inputVatAccount, "ASSET", "DEBIT"))
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["inputVatAccountId"] = ["إذا اخترت فصل VAT، فيجب أن يكون حساب ضريبة المدخلات أصلًا مدينًا ونشطًا وقابلًا للترحيل. تحقق من قابلية الاسترداد خارج النظام قبل اختياره."]
                    });
            }

            var entryNumberLock = $"journal-entry-number:{tenant.TenantId:D}:{companyId:D}:{entryNumber}";
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({entryNumberLock}, 0))", token);

            if (await db.JournalEntries.AnyAsync(
                    x => x.TenantId == tenant.TenantId && x.CompanyId == companyId && x.EntryNumber == entryNumber, token))
                return Results.Conflict(new { error = "journal_entry_number_conflict" });

            if (await db.JournalEntries.AnyAsync(
                    x => x.TenantId == tenant.TenantId && x.CompanyId == companyId &&
                         x.SourceType == "PURCHASE_INVOICE_DRAFT" && x.SourceId == invoiceId, token))
                return Results.Conflict(new { error = "purchase_draft_already_has_journal" });

            var postedAt = DateTime.UtcNow;
            var entry = new JournalEntryRecord
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.TenantId,
                CompanyId = companyId,
                FiscalPeriodId = period.Id,
                EntryNumber = entryNumber,
                EntryDate = draft.IssueDate,
                Description = $"Purchase draft {invoiceId:D}",
                CurrencyCode = company.BaseCurrency.Trim().ToUpperInvariant(),
                Status = "DRAFT",
                SourceType = "PURCHASE_INVOICE_DRAFT",
                SourceId = invoiceId,
                CreatedAt = postedAt
            };
            db.JournalEntries.Add(entry);
            db.JournalEntryLines.Add(new JournalEntryLineRecord
            {
                Id = Guid.NewGuid(), TenantId = tenant.TenantId, CompanyId = companyId,
                JournalEntryId = entry.Id, AccountId = purchaseDebit.Id, LineNumber = 1,
                Description = postVatAsAsset ? "Purchase cost excluding input VAT" : "Purchase cost including VAT",
                Debit = purchaseDebitAmount, Credit = 0m
            });
            if (postVatAsAsset && inputVatAccount is not null)
            {
                db.JournalEntryLines.Add(new JournalEntryLineRecord
                {
                    Id = Guid.NewGuid(), TenantId = tenant.TenantId, CompanyId = companyId,
                    JournalEntryId = entry.Id, AccountId = inputVatAccount.Id, LineNumber = 2,
                    Description = "Input VAT asset (user-selected)",
                    Debit = vat, Credit = 0m
                });
            }
            db.JournalEntryLines.Add(new JournalEntryLineRecord
            {
                Id = Guid.NewGuid(), TenantId = tenant.TenantId, CompanyId = companyId,
                JournalEntryId = entry.Id, AccountId = payable.Id, LineNumber = postVatAsAsset ? 3 : 2,
                Description = "Accounts payable",
                Debit = 0m, Credit = total
            });

            await db.SaveChangesAsync(token);

            entry.Status = "APPROVED";
            await db.SaveChangesAsync(token);

            entry.Status = "POSTED";
            entry.PostedAt = postedAt;
            await db.SaveChangesAsync(token);

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE purchase_invoice_drafts
                   SET status = 'ACCOUNTING_POSTED',
                       accounting_posted_journal_entry_id = {entry.Id},
                       accounting_posted_at = {postedAt},
                       accounting_input_vat_account_id = {inputVatAccount?.Id}
                 WHERE tenant_id = {tenant.TenantId}
                   AND company_id = {companyId}
                   AND id = {invoiceId}
                """, token);

            db.AuditLogs.Add(new AuditLogRecord
            {
                Id = Guid.NewGuid(), TenantId = tenant.TenantId, UserId = tenant.UserId,
                Action = "PURCHASE_DRAFT_POSTED_TO_GL", EntityType = "PurchaseInvoiceDraft",
                EntityId = invoiceId.ToString("D"),
                IpAddress = http.Connection.RemoteIpAddress?.ToString(),
                DetailsJson = JsonSerializer.Serialize(new
                {
                    JournalEntryId = entry.Id,
                    EntryNumber = entry.EntryNumber,
                    PurchaseDebitAccountId = purchaseDebit.Id,
                    PayableAccountId = payable.Id,
                    InputVatAccountId = inputVatAccount?.Id,
                    InputVatPostedToAsset = postVatAsAsset,
                    GrossAmount = gross,
                    DiscountAmount = discount,
                    NetAmount = net,
                    VatAmount = vat,
                    TotalAmount = total,
                    InputVatPostingMode = postVatAsAsset ? "ASSET_ACCOUNT_SELECTED_BY_USER" : "INCLUDED_IN_PURCHASE_COST"
                }),
                CreatedAt = postedAt
            });
            await db.SaveChangesAsync(token);

            return Results.Created(
                $"/api/v1/journal-entries/{entry.Id}",
                ToResult(draft with
                {
                    Status = "ACCOUNTING_POSTED",
                    AccountingPostedJournalEntryId = entry.Id,
                    AccountingPostedAt = postedAt,
                    AccountingInputVatAccountId = inputVatAccount?.Id
                }, entry, alreadyPosted: false));
        }, ct);
    }

    private static bool IsPurchaseDebitPostable(AccountRecord account) =>
        (account.AccountType == "ASSET" || account.AccountType == "EXPENSE") &&
        account.NormalBalance == "DEBIT" && account.IsActive && account.AllowPosting;

    private static bool IsPostable(AccountRecord account, string type, string normalBalance) =>
        account.AccountType == type && account.NormalBalance == normalBalance &&
        account.IsActive && account.AllowPosting;

    private static PurchaseDraftLedgerPostingResult ToResult(
        PurchaseDraftPostingSource draft, JournalEntryRecord entry, bool alreadyPosted) =>
        new(draft.Id, entry.Id, entry.EntryNumber, entry.Status, entry.EntryDate,
            draft.GrossAmount ?? 0m, draft.DiscountAmount ?? 0m, draft.NetAmount ?? 0m,
            draft.VatAmount ?? 0m, draft.TotalAmount ?? 0m, alreadyPosted,
            draft.AccountingInputVatAccountId,
            draft.AccountingInputVatAccountId.HasValue
                ? "POSTED_TO_USER_SELECTED_ASSET_ACCOUNT"
                : "INCLUDED_IN_PURCHASE_COST",
            draft.AccountingInputVatAccountId.HasValue
                ? "تم ترحيل القيد الداخلي وفصل VAT في حساب أصل اختاره المستخدم. تحقق من صحة فاتورة المورد وقابلية استرداد ضريبة المدخلات قبل اختياره."
                : "تم ترحيل القيد الداخلي مع تحميل VAT ضمن تكلفة حساب الشراء؛ لا يفترض النظام تلقائيًا قابلية استرداد ضريبة المدخلات.");

    public sealed record PostPurchaseDraftToLedgerRequest(
        Guid FiscalPeriodId, string? EntryNumber, Guid PurchaseDebitAccountId,
        Guid PayableAccountId, Guid? InputVatAccountId = null);

    public sealed record PurchaseDraftLedgerPostingResult(
        Guid PurchaseInvoiceDraftId, Guid JournalEntryId, string EntryNumber,
        string JournalEntryStatus, DateOnly EntryDate,
        decimal GrossAmount, decimal DiscountAmount, decimal NetAmount, decimal VatAmount, decimal TotalAmount,
        bool AlreadyPosted, Guid? InputVatAccountId, string InputVatPostingMode, string Message);

    public sealed class PurchaseDraftPostingSource
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
        public Guid? AccountingInputVatAccountId { get; init; }
    }
}
