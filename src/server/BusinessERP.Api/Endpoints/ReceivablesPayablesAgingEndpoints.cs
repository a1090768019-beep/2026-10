using BusinessERP.Application.Abstractions;
using BusinessERP.Infrastructure.Persistence;
using BusinessERP.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace BusinessERP.Api.Endpoints;

/// <summary>
/// Historical open-invoice aging based on posted invoice balances and posted settlements as of a
/// requested document date. It is a bookkeeping report, not a collections forecast or bank statement.
/// </summary>
public static class ReceivablesPayablesAgingEndpoints
{
    private static readonly string[] AllowedRoles = ["OWNER", "ADMIN", "ACCOUNTANT"];

    public static IEndpointRouteBuilder MapReceivablesPayablesAgingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/companies/{companyId:guid}/reports/receivables-aging", GetReceivablesAging)
            .WithName("GetReceivablesAging").WithTags("Financial reports");
        endpoints.MapGet("/api/v1/companies/{companyId:guid}/reports/payables-aging", GetPayablesAging)
            .WithName("GetPayablesAging").WithTags("Financial reports");
        return endpoints;
    }

    private static async Task<IResult> GetReceivablesAging(
        Guid companyId, ITenantContext tenant, TenantTransactionRunner runner, CancellationToken ct,
        DateOnly? asOfDate = null, int page = 1, int pageSize = 100)
    {
        if (!AllowedRoles.Contains(tenant.RoleCode, StringComparer.OrdinalIgnoreCase))
            return Results.Forbid();
        if (!TryValidateReportRequest(asOfDate, page, pageSize, out var reportDate, out var offset, out var error))
            return Results.ValidationProblem(error!);

        return await runner.ExecuteAsync<IResult>(async (db, token) =>
        {
            if (!await db.Companies.AsNoTracking()
                    .AnyAsync(x => x.Id == companyId && x.TenantId == tenant.TenantId, token))
                return Results.NotFound(new { error = "company_not_found" });

            var totals = await db.Database.SqlQuery<AgingTotalsRow>($"""
                WITH invoice_balances AS (
                    SELECT i.id AS invoice_id, i.invoice_number AS document_number,
                           i.issue_date, i.due_date, i.customer_id AS party_id, c.name AS party_name,
                           i.currency_code, i.total_amount AS invoice_amount,
                           COALESCE(settled.settled_amount, 0)::numeric(20,2) AS settled_amount,
                           (i.total_amount - COALESCE(settled.settled_amount, 0))::numeric(20,2) AS outstanding_amount
                      FROM sales_invoice_drafts i
                      JOIN customers c ON c.tenant_id=i.tenant_id AND c.company_id=i.company_id AND c.id=i.customer_id
                      LEFT JOIN (
                          SELECT a.tenant_id, a.company_id, a.sales_invoice_draft_id AS invoice_id,
                                 SUM(a.allocated_amount)::numeric(20,2) AS settled_amount
                            FROM customer_receipt_allocations a
                            JOIN customer_receipts r ON r.tenant_id=a.tenant_id AND r.company_id=a.company_id AND r.id=a.receipt_id
                            JOIN journal_entries settlement_je ON settlement_je.tenant_id=r.tenant_id AND settlement_je.company_id=r.company_id
                                AND settlement_je.id=r.accounting_posted_journal_entry_id AND settlement_je.status='POSTED'
                           WHERE a.tenant_id={tenant.TenantId} AND a.company_id={companyId}
                             AND r.status='ACCOUNTING_POSTED' AND settlement_je.entry_date <= {reportDate}
                           GROUP BY a.tenant_id, a.company_id, a.sales_invoice_draft_id
                      ) settled ON settled.tenant_id=i.tenant_id AND settled.company_id=i.company_id AND settled.invoice_id=i.id
                     WHERE i.tenant_id={tenant.TenantId} AND i.company_id={companyId}
                       AND i.status='ACCOUNTING_POSTED' AND i.issue_date <= {reportDate}
                       AND EXISTS (SELECT 1 FROM journal_entries invoice_je
                                    WHERE invoice_je.tenant_id=i.tenant_id AND invoice_je.company_id=i.company_id
                                      AND invoice_je.id=i.accounting_posted_journal_entry_id
                                      AND invoice_je.status='POSTED' AND invoice_je.entry_date <= {reportDate})
                ), open_balances AS (
                    SELECT *,
                           CASE WHEN due_date IS NULL OR due_date >= {reportDate} THEN 0
                                ELSE {reportDate} - due_date END AS days_past_due,
                           CASE WHEN due_date IS NULL OR due_date >= {reportDate} THEN 'CURRENT'
                                WHEN {reportDate} - due_date <= 30 THEN 'DAYS_1_30'
                                WHEN {reportDate} - due_date <= 60 THEN 'DAYS_31_60'
                                WHEN {reportDate} - due_date <= 90 THEN 'DAYS_61_90'
                                ELSE 'OVER_90' END AS aging_bucket
                      FROM invoice_balances
                     WHERE outstanding_amount > 0
                )
                SELECT COUNT(*)::bigint AS "TotalRows",
                       COALESCE(SUM(outstanding_amount),0)::numeric(20,2) AS "TotalOutstandingAmount",
                       COALESCE(SUM(CASE WHEN aging_bucket='CURRENT' THEN outstanding_amount ELSE 0 END),0)::numeric(20,2) AS "CurrentAmount",
                       COALESCE(SUM(CASE WHEN aging_bucket='DAYS_1_30' THEN outstanding_amount ELSE 0 END),0)::numeric(20,2) AS "Days1To30Amount",
                       COALESCE(SUM(CASE WHEN aging_bucket='DAYS_31_60' THEN outstanding_amount ELSE 0 END),0)::numeric(20,2) AS "Days31To60Amount",
                       COALESCE(SUM(CASE WHEN aging_bucket='DAYS_61_90' THEN outstanding_amount ELSE 0 END),0)::numeric(20,2) AS "Days61To90Amount",
                       COALESCE(SUM(CASE WHEN aging_bucket='OVER_90' THEN outstanding_amount ELSE 0 END),0)::numeric(20,2) AS "Over90Amount"
                  FROM open_balances
                """).SingleAsync(token);

            var rows = await db.Database.SqlQuery<InvoiceAgingRow>($"""
                WITH invoice_balances AS (
                    SELECT i.id AS "InvoiceId", i.invoice_number AS "DocumentNumber",
                           i.issue_date AS "IssueDate", i.due_date AS "DueDate",
                           i.customer_id AS "PartyId", c.name AS "PartyName", i.currency_code AS "CurrencyCode",
                           i.total_amount AS "InvoiceAmount",
                           COALESCE(settled.settled_amount, 0)::numeric(20,2) AS "SettledAmount",
                           (i.total_amount - COALESCE(settled.settled_amount, 0))::numeric(20,2) AS "OutstandingAmount"
                      FROM sales_invoice_drafts i
                      JOIN customers c ON c.tenant_id=i.tenant_id AND c.company_id=i.company_id AND c.id=i.customer_id
                      LEFT JOIN (
                          SELECT a.tenant_id, a.company_id, a.sales_invoice_draft_id AS invoice_id,
                                 SUM(a.allocated_amount)::numeric(20,2) AS settled_amount
                            FROM customer_receipt_allocations a
                            JOIN customer_receipts r ON r.tenant_id=a.tenant_id AND r.company_id=a.company_id AND r.id=a.receipt_id
                            JOIN journal_entries settlement_je ON settlement_je.tenant_id=r.tenant_id AND settlement_je.company_id=r.company_id
                                AND settlement_je.id=r.accounting_posted_journal_entry_id AND settlement_je.status='POSTED'
                           WHERE a.tenant_id={tenant.TenantId} AND a.company_id={companyId}
                             AND r.status='ACCOUNTING_POSTED' AND settlement_je.entry_date <= {reportDate}
                           GROUP BY a.tenant_id, a.company_id, a.sales_invoice_draft_id
                      ) settled ON settled.tenant_id=i.tenant_id AND settled.company_id=i.company_id AND settled.invoice_id=i.id
                     WHERE i.tenant_id={tenant.TenantId} AND i.company_id={companyId}
                       AND i.status='ACCOUNTING_POSTED' AND i.issue_date <= {reportDate}
                       AND EXISTS (SELECT 1 FROM journal_entries invoice_je
                                    WHERE invoice_je.tenant_id=i.tenant_id AND invoice_je.company_id=i.company_id
                                      AND invoice_je.id=i.accounting_posted_journal_entry_id
                                      AND invoice_je.status='POSTED' AND invoice_je.entry_date <= {reportDate})
                ), open_balances AS (
                    SELECT *,
                           CASE WHEN due_date IS NULL OR due_date >= {reportDate} THEN 0
                                ELSE {reportDate} - due_date END AS "DaysPastDue",
                           CASE WHEN due_date IS NULL OR due_date >= {reportDate} THEN 'CURRENT'
                                WHEN {reportDate} - due_date <= 30 THEN 'DAYS_1_30'
                                WHEN {reportDate} - due_date <= 60 THEN 'DAYS_31_60'
                                WHEN {reportDate} - due_date <= 90 THEN 'DAYS_61_90'
                                ELSE 'OVER_90' END AS "AgingBucket"
                      FROM invoice_balances
                     WHERE "OutstandingAmount" > 0
                )
                SELECT "InvoiceId", "DocumentNumber", "IssueDate", "DueDate", "PartyId", "PartyName",
                       "CurrencyCode", "InvoiceAmount", "SettledAmount", "OutstandingAmount", "DaysPastDue", "AgingBucket"
                  FROM open_balances
                 ORDER BY "DueDate" NULLS LAST, "IssueDate", "DocumentNumber", "InvoiceId"
                 LIMIT {pageSize} OFFSET {offset}
                """).ToListAsync(token);

            return Results.Ok(BuildReport(reportDate, page, pageSize, totals, rows));
        }, ct);
    }

    private static async Task<IResult> GetPayablesAging(
        Guid companyId, ITenantContext tenant, TenantTransactionRunner runner, CancellationToken ct,
        DateOnly? asOfDate = null, int page = 1, int pageSize = 100)
    {
        if (!AllowedRoles.Contains(tenant.RoleCode, StringComparer.OrdinalIgnoreCase))
            return Results.Forbid();
        if (!TryValidateReportRequest(asOfDate, page, pageSize, out var reportDate, out var offset, out var error))
            return Results.ValidationProblem(error!);

        return await runner.ExecuteAsync<IResult>(async (db, token) =>
        {
            if (!await db.Companies.AsNoTracking()
                    .AnyAsync(x => x.Id == companyId && x.TenantId == tenant.TenantId, token))
                return Results.NotFound(new { error = "company_not_found" });

            var totals = await db.Database.SqlQuery<AgingTotalsRow>($"""
                WITH invoice_balances AS (
                    SELECT i.id AS invoice_id, i.purchase_number AS document_number,
                           i.issue_date, i.due_date, i.supplier_id AS party_id, s.name AS party_name,
                           i.currency_code, i.total_amount AS invoice_amount,
                           COALESCE(settled.settled_amount, 0)::numeric(20,2) AS settled_amount,
                           (i.total_amount - COALESCE(settled.settled_amount, 0))::numeric(20,2) AS outstanding_amount
                      FROM purchase_invoice_drafts i
                      JOIN suppliers s ON s.tenant_id=i.tenant_id AND s.company_id=i.company_id AND s.id=i.supplier_id
                      LEFT JOIN (
                          SELECT a.tenant_id, a.company_id, a.purchase_invoice_draft_id AS invoice_id,
                                 SUM(a.allocated_amount)::numeric(20,2) AS settled_amount
                            FROM supplier_payment_allocations a
                            JOIN supplier_payments p ON p.tenant_id=a.tenant_id AND p.company_id=a.company_id AND p.id=a.payment_id
                            JOIN journal_entries settlement_je ON settlement_je.tenant_id=p.tenant_id AND settlement_je.company_id=p.company_id
                                AND settlement_je.id=p.accounting_posted_journal_entry_id AND settlement_je.status='POSTED'
                           WHERE a.tenant_id={tenant.TenantId} AND a.company_id={companyId}
                             AND p.status='ACCOUNTING_POSTED' AND settlement_je.entry_date <= {reportDate}
                           GROUP BY a.tenant_id, a.company_id, a.purchase_invoice_draft_id
                      ) settled ON settled.tenant_id=i.tenant_id AND settled.company_id=i.company_id AND settled.invoice_id=i.id
                     WHERE i.tenant_id={tenant.TenantId} AND i.company_id={companyId}
                       AND i.status='ACCOUNTING_POSTED' AND i.issue_date <= {reportDate}
                       AND EXISTS (SELECT 1 FROM journal_entries invoice_je
                                    WHERE invoice_je.tenant_id=i.tenant_id AND invoice_je.company_id=i.company_id
                                      AND invoice_je.id=i.accounting_posted_journal_entry_id
                                      AND invoice_je.status='POSTED' AND invoice_je.entry_date <= {reportDate})
                ), open_balances AS (
                    SELECT *,
                           CASE WHEN due_date IS NULL OR due_date >= {reportDate} THEN 0
                                ELSE {reportDate} - due_date END AS days_past_due,
                           CASE WHEN due_date IS NULL OR due_date >= {reportDate} THEN 'CURRENT'
                                WHEN {reportDate} - due_date <= 30 THEN 'DAYS_1_30'
                                WHEN {reportDate} - due_date <= 60 THEN 'DAYS_31_60'
                                WHEN {reportDate} - due_date <= 90 THEN 'DAYS_61_90'
                                ELSE 'OVER_90' END AS aging_bucket
                      FROM invoice_balances
                     WHERE outstanding_amount > 0
                )
                SELECT COUNT(*)::bigint AS "TotalRows",
                       COALESCE(SUM(outstanding_amount),0)::numeric(20,2) AS "TotalOutstandingAmount",
                       COALESCE(SUM(CASE WHEN aging_bucket='CURRENT' THEN outstanding_amount ELSE 0 END),0)::numeric(20,2) AS "CurrentAmount",
                       COALESCE(SUM(CASE WHEN aging_bucket='DAYS_1_30' THEN outstanding_amount ELSE 0 END),0)::numeric(20,2) AS "Days1To30Amount",
                       COALESCE(SUM(CASE WHEN aging_bucket='DAYS_31_60' THEN outstanding_amount ELSE 0 END),0)::numeric(20,2) AS "Days31To60Amount",
                       COALESCE(SUM(CASE WHEN aging_bucket='DAYS_61_90' THEN outstanding_amount ELSE 0 END),0)::numeric(20,2) AS "Days61To90Amount",
                       COALESCE(SUM(CASE WHEN aging_bucket='OVER_90' THEN outstanding_amount ELSE 0 END),0)::numeric(20,2) AS "Over90Amount"
                  FROM open_balances
                """).SingleAsync(token);

            var rows = await db.Database.SqlQuery<InvoiceAgingRow>($"""
                WITH invoice_balances AS (
                    SELECT i.id AS "InvoiceId", i.purchase_number AS "DocumentNumber",
                           i.issue_date AS "IssueDate", i.due_date AS "DueDate",
                           i.supplier_id AS "PartyId", s.name AS "PartyName", i.currency_code AS "CurrencyCode",
                           i.total_amount AS "InvoiceAmount",
                           COALESCE(settled.settled_amount, 0)::numeric(20,2) AS "SettledAmount",
                           (i.total_amount - COALESCE(settled.settled_amount, 0))::numeric(20,2) AS "OutstandingAmount"
                      FROM purchase_invoice_drafts i
                      JOIN suppliers s ON s.tenant_id=i.tenant_id AND s.company_id=i.company_id AND s.id=i.supplier_id
                      LEFT JOIN (
                          SELECT a.tenant_id, a.company_id, a.purchase_invoice_draft_id AS invoice_id,
                                 SUM(a.allocated_amount)::numeric(20,2) AS settled_amount
                            FROM supplier_payment_allocations a
                            JOIN supplier_payments p ON p.tenant_id=a.tenant_id AND p.company_id=a.company_id AND p.id=a.payment_id
                            JOIN journal_entries settlement_je ON settlement_je.tenant_id=p.tenant_id AND settlement_je.company_id=p.company_id
                                AND settlement_je.id=p.accounting_posted_journal_entry_id AND settlement_je.status='POSTED'
                           WHERE a.tenant_id={tenant.TenantId} AND a.company_id={companyId}
                             AND p.status='ACCOUNTING_POSTED' AND settlement_je.entry_date <= {reportDate}
                           GROUP BY a.tenant_id, a.company_id, a.purchase_invoice_draft_id
                      ) settled ON settled.tenant_id=i.tenant_id AND settled.company_id=i.company_id AND settled.invoice_id=i.id
                     WHERE i.tenant_id={tenant.TenantId} AND i.company_id={companyId}
                       AND i.status='ACCOUNTING_POSTED' AND i.issue_date <= {reportDate}
                       AND EXISTS (SELECT 1 FROM journal_entries invoice_je
                                    WHERE invoice_je.tenant_id=i.tenant_id AND invoice_je.company_id=i.company_id
                                      AND invoice_je.id=i.accounting_posted_journal_entry_id
                                      AND invoice_je.status='POSTED' AND invoice_je.entry_date <= {reportDate})
                ), open_balances AS (
                    SELECT *,
                           CASE WHEN due_date IS NULL OR due_date >= {reportDate} THEN 0
                                ELSE {reportDate} - due_date END AS "DaysPastDue",
                           CASE WHEN due_date IS NULL OR due_date >= {reportDate} THEN 'CURRENT'
                                WHEN {reportDate} - due_date <= 30 THEN 'DAYS_1_30'
                                WHEN {reportDate} - due_date <= 60 THEN 'DAYS_31_60'
                                WHEN {reportDate} - due_date <= 90 THEN 'DAYS_61_90'
                                ELSE 'OVER_90' END AS "AgingBucket"
                      FROM invoice_balances
                     WHERE "OutstandingAmount" > 0
                )
                SELECT "InvoiceId", "DocumentNumber", "IssueDate", "DueDate", "PartyId", "PartyName",
                       "CurrencyCode", "InvoiceAmount", "SettledAmount", "OutstandingAmount", "DaysPastDue", "AgingBucket"
                  FROM open_balances
                 ORDER BY "DueDate" NULLS LAST, "IssueDate", "DocumentNumber", "InvoiceId"
                 LIMIT {pageSize} OFFSET {offset}
                """).ToListAsync(token);

            return Results.Ok(BuildReport(reportDate, page, pageSize, totals, rows));
        }, ct);
    }

    private static bool TryValidateReportRequest(
        DateOnly? requestedDate, int page, int pageSize,
        out DateOnly reportDate, out long offset, out Dictionary<string, string[]>? errors)
    {
        reportDate = requestedDate ?? default;
        offset = 0;
        errors = null;
        var validation = new Dictionary<string, string[]>();
        if (requestedDate is null)
            validation["asOfDate"] = ["حدد تاريخ التقرير بصيغة YYYY-MM-DD."];
        if (page < 1)
            validation["page"] = ["رقم الصفحة يجب أن يكون 1 أو أكثر."];
        if (pageSize is < 1 or > 200)
            validation["pageSize"] = ["حجم الصفحة يجب أن يكون بين 1 و200."];
        if (page >= 1 && pageSize is >= 1 and <= 200)
        {
            offset = ((long)page - 1L) * pageSize;
            if (offset > int.MaxValue)
                validation["page"] = ["رقم الصفحة يتجاوز الحد المسموح."];
        }
        if (validation.Count > 0)
        {
            errors = validation;
            return false;
        }
        return true;
    }

    private static InvoiceAgingApiReport BuildReport(
        DateOnly asOfDate, int page, int pageSize, AgingTotalsRow totals, IReadOnlyList<InvoiceAgingRow> rows)
    {
        var totalPages = totals.TotalRows == 0 ? 0 : (int)Math.Ceiling(totals.TotalRows / (double)pageSize);
        return new InvoiceAgingApiReport(asOfDate, page, pageSize, totals.TotalRows, totalPages,
            totals.TotalOutstandingAmount, totals.CurrentAmount, totals.Days1To30Amount,
            totals.Days31To60Amount, totals.Days61To90Amount, totals.Over90Amount, rows);
    }

    private sealed record AgingTotalsRow(long TotalRows, decimal TotalOutstandingAmount, decimal CurrentAmount,
        decimal Days1To30Amount, decimal Days31To60Amount, decimal Days61To90Amount, decimal Over90Amount);
}

public sealed record InvoiceAgingApiReport(DateOnly AsOfDate, int Page, int PageSize, long TotalRows, int TotalPages,
    decimal TotalOutstandingAmount, decimal CurrentAmount, decimal Days1To30Amount, decimal Days31To60Amount,
    decimal Days61To90Amount, decimal Over90Amount, IReadOnlyList<InvoiceAgingRow> Rows);

public sealed record InvoiceAgingRow(Guid InvoiceId, string DocumentNumber, DateOnly IssueDate, DateOnly? DueDate,
    Guid PartyId, string PartyName, string CurrencyCode, decimal InvoiceAmount, decimal SettledAmount,
    decimal OutstandingAmount, int DaysPastDue, string AgingBucket);
