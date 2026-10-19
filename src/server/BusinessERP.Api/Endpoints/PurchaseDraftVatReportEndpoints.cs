using BusinessERP.Application.Abstractions;
using BusinessERP.Infrastructure.Persistence;
using BusinessERP.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace BusinessERP.Api.Endpoints;

/// <summary>
/// Read-only VAT details report based on saved VAT calculations on internal purchase drafts.
/// It deliberately does not describe these drafts as issued tax invoices or ZATCA-cleared documents.
/// </summary>
public static class PurchaseDraftVatReportEndpoints
{
    private const int MaximumPageSize = 200;
    private const int MaximumPageNumber = 100_000;

    public static IEndpointRouteBuilder MapPurchaseDraftVatReportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
                "/api/v1/companies/{companyId:guid}/reports/purchase-vat-details",
                GetPurchaseVatDetailsReport)
            .WithName("GetPurchaseVatDetailsReport")
            .WithTags("Purchase reports");
        return endpoints;
    }

    private static async Task<IResult> GetPurchaseVatDetailsReport(
        Guid companyId,
        DateOnly fromDate,
        DateOnly toDate,
        ITenantContext tenant,
        TenantTransactionRunner runner,
        CancellationToken ct,
        int page = 1,
        int pageSize = 100)
    {
        if (fromDate > toDate)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["dateRange"] = ["تاريخ البداية يجب ألا يتجاوز تاريخ النهاية."]
            });

        if (fromDate < new DateOnly(1900, 1, 1) || toDate > new DateOnly(2200, 12, 31) ||
            toDate.DayNumber - fromDate.DayNumber > 3660)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["dateRange"] = ["نطاق التقرير يجب أن يكون داخل التواريخ المدعومة وألا يتجاوز عشر سنوات."]
            });

        if (page is < 1 or > MaximumPageNumber)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["page"] = [$"رقم الصفحة يجب أن يكون بين 1 و{MaximumPageNumber}."]
            });

        if (pageSize is < 1 or > MaximumPageSize)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["pageSize"] = [$"حجم الصفحة يجب أن يكون بين 1 و{MaximumPageSize}."]
            });

        var offset = checked((page - 1) * pageSize);
        return await runner.ExecuteAsync<IResult>(async (db, token) =>
        {
            var companyExists = await db.Companies.AsNoTracking()
                .AnyAsync(x => x.Id == companyId && x.TenantId == tenant.TenantId, token);
            if (!companyExists)
                return Results.NotFound(new { error = "company_not_found" });

            var totals = await db.Database.SqlQuery<PurchaseVatReportTotals>($"""
                SELECT count(*)::bigint AS "InvoiceCount",
                       COALESCE(sum(invoice.gross_amount), 0) AS "GrossAmount",
                       COALESCE(sum(invoice.discount_amount), 0) AS "DiscountAmount",
                       COALESCE(sum(invoice.net_amount), 0) AS "NetAmount",
                       COALESCE(sum(invoice.vat_amount), 0) AS "VatAmount",
                       COALESCE(sum(invoice.total_amount), 0) AS "TotalAmount"
                  FROM purchase_invoice_drafts invoice
                 WHERE invoice.tenant_id = {tenant.TenantId}
                   AND invoice.company_id = {companyId}
                   AND invoice.issue_date >= {fromDate}
                   AND invoice.issue_date <= {toDate}
                   AND invoice.tax_calculation_status = 'CALCULATED'
                """).SingleAsync(token);

            var totalLines = await db.Database.SqlQuery<long>($"""
                SELECT count(*)::bigint AS "Value"
                  FROM purchase_invoice_drafts invoice
                  JOIN purchase_invoice_draft_lines line
                    ON line.tenant_id = invoice.tenant_id
                   AND line.company_id = invoice.company_id
                   AND line.sales_invoice_draft_id = invoice.id
                 WHERE invoice.tenant_id = {tenant.TenantId}
                   AND invoice.company_id = {companyId}
                   AND invoice.issue_date >= {fromDate}
                   AND invoice.issue_date <= {toDate}
                   AND invoice.tax_calculation_status = 'CALCULATED'
                """).SingleAsync(token);

            var rows = await db.Database.SqlQuery<PurchaseVatReportLine>($"""
                SELECT invoice.id AS "InvoiceId",
                       invoice.purchase_number AS "PurchaseNumber",
                       invoice.supplier_invoice_number AS "SupplierInvoiceNumber",
                       invoice.issue_date AS "IssueDate",
                       invoice.supplier_name_snapshot AS "SupplierName",
                       invoice.supplier_tax_number_snapshot AS "SupplierTaxNumber",
                       invoice.currency_code AS "CurrencyCode",
                       invoice.status AS "DocumentStatus",
                       line.line_number AS "LineNumber",
                       line.description AS "Description",
                       line.quantity AS "Quantity",
                       line.unit_price AS "UnitPrice",
                       line.gross_amount AS "GrossAmount",
                       line.discount_amount AS "DiscountAmount",
                       line.vat_treatment AS "VatTreatment",
                       line.vat_treatment_reason AS "VatTreatmentReason",
                       line.net_amount AS "NetAmount",
                       line.vat_rate_percent AS "VatRatePercent",
                       line.vat_amount AS "VatAmount",
                       line.total_amount AS "TotalAmount"
                  FROM purchase_invoice_drafts invoice
                  JOIN purchase_invoice_draft_lines line
                    ON line.tenant_id = invoice.tenant_id
                   AND line.company_id = invoice.company_id
                   AND line.sales_invoice_draft_id = invoice.id
                 WHERE invoice.tenant_id = {tenant.TenantId}
                   AND invoice.company_id = {companyId}
                   AND invoice.issue_date >= {fromDate}
                   AND invoice.issue_date <= {toDate}
                   AND invoice.tax_calculation_status = 'CALCULATED'
                 ORDER BY invoice.issue_date DESC, invoice.purchase_number, line.line_number
                 LIMIT {pageSize} OFFSET {offset}
                """).ToListAsync(token);

            var pageCount = totalLines == 0 ? 1 : (int)Math.Ceiling(totalLines / (double)pageSize);
            return Results.Ok(new PurchaseVatDetailsReportDto(
                companyId, fromDate, toDate, page, pageSize, totalLines, pageCount,
                totals.InvoiceCount, totals.GrossAmount, totals.DiscountAmount, totals.NetAmount,
                totals.VatAmount, totals.TotalAmount, "INTERNAL_PURCHASE_DRAFT_VAT_REPORT",
                "تقرير داخلي من مسودات مشتريات تم احتساب VAT لها. لا يثبت صلاحية فاتورة المورد أو قابلية استرداد ضريبة المدخلات، ولا يُنشئ قيدًا محاسبيًا.",
                rows));
        }, ct);
    }

    private sealed record PurchaseVatReportTotals(
        long InvoiceCount,
        decimal GrossAmount,
        decimal DiscountAmount,
        decimal NetAmount,
        decimal VatAmount,
        decimal TotalAmount);

    private sealed record PurchaseVatReportLine(
        Guid InvoiceId,
        string PurchaseNumber,
        string SupplierInvoiceNumber,
        DateOnly IssueDate,
        string SupplierName,
        string? SupplierTaxNumber,
        string CurrencyCode,
        string DocumentStatus,
        int LineNumber,
        string Description,
        decimal Quantity,
        decimal UnitPrice,
        decimal GrossAmount,
        decimal DiscountAmount,
        string VatTreatment,
        string? VatTreatmentReason,
        decimal NetAmount,
        decimal VatRatePercent,
        decimal VatAmount,
        decimal TotalAmount);

    private sealed record PurchaseVatDetailsReportDto(
        Guid CompanyId,
        DateOnly FromDate,
        DateOnly ToDate,
        int Page,
        int PageSize,
        long TotalLines,
        int TotalPages,
        long InvoiceCount,
        decimal GrossAmount,
        decimal DiscountAmount,
        decimal NetAmount,
        decimal VatAmount,
        decimal TotalAmount,
        string ReportKind,
        string Message,
        IReadOnlyList<PurchaseVatReportLine> Rows);
}
