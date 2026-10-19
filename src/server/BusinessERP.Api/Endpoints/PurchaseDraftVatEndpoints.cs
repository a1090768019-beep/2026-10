using System.Text.Json;
using BusinessERP.Application.Abstractions;
using BusinessERP.Domain.Tax;
using BusinessERP.Infrastructure.Persistence;
using BusinessERP.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace BusinessERP.Api.Endpoints;

/// <summary>
/// Saves an internal VAT calculation against a purchase draft. The result does not determine
/// legal invoice validity or input-tax recoverability.
/// </summary>
public static class PurchaseDraftVatEndpoints
{
    private static readonly string[] AccountingRoles = ["OWNER", "ADMIN", "ACCOUNTANT"];

    public static IEndpointRouteBuilder MapPurchaseDraftVatEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
                "/api/v1/companies/{companyId:guid}/purchase-invoice-drafts/{invoiceId:guid}/calculate-vat",
                CalculateAndPersistVat)
            .WithName("CalculateAndPersistPurchaseDraftVat")
            .WithTags("Purchases - VAT");
        return endpoints;
    }

    private static async Task<IResult> CalculateAndPersistVat(
        Guid companyId, Guid invoiceId, ApplyPurchaseDraftVatRequest request,
        ITenantContext tenant, HttpContext http, TenantTransactionRunner runner, CancellationToken ct)
    {
        if (!AccountingRoles.Contains(tenant.RoleCode, StringComparer.OrdinalIgnoreCase))
            return Results.Forbid();
        if (request.Lines is null || request.Lines.Count is < 1 or > SaudiVatCalculator.MaximumLines ||
            request.Lines.Any(line => line is null))
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["lines"] = [$"أرسل من 1 إلى {SaudiVatCalculator.MaximumLines} بندًا مطابقًا لبنود المسودة."]
            });

        var duplicateLines = request.Lines.GroupBy(line => line!.LineNumber)
            .Any(group => group.Key < 1 || group.Count() > 1);
        if (duplicateLines)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["lines"] = ["أرقام البنود يجب أن تكون موجبة وفريدة."]
            });

        var normalized = new List<RequestedVatLine>(request.Lines.Count);
        var requestErrors = new Dictionary<string, string[]>();
        foreach (var (line, index) in request.Lines.Select((value, index) => (value!, index)))
        {
            if (line.DiscountAmount < 0m ||
                decimal.Round(line.DiscountAmount, SaudiVatCalculator.MoneyScale, MidpointRounding.ToEven) != line.DiscountAmount)
            {
                requestErrors[$"lines[{index}].discountAmount"] = ["الخصم يجب أن يكون غير سالب وبحد أقصى منزلتين عشريتين."];
                continue;
            }
            if (!TryParseTreatment(line.Treatment, out var treatment))
            {
                requestErrors[$"lines[{index}].treatment"] = ["تصنيف VAT غير معروف."];
                continue;
            }
            if (treatment != SalesVatTreatment.StandardRated && string.IsNullOrWhiteSpace(line.TreatmentReason))
            {
                requestErrors[$"lines[{index}].treatmentReason"] = ["السبب مطلوب عند استخدام تصنيف غير المعدل القياسي."];
                continue;
            }
            if (line.TreatmentReason?.Length > 500)
            {
                requestErrors[$"lines[{index}].treatmentReason"] = ["سبب المعاملة لا يتجاوز 500 حرف."];
                continue;
            }
            normalized.Add(new RequestedVatLine(line.LineNumber, line.DiscountAmount, treatment,
                string.IsNullOrWhiteSpace(line.TreatmentReason) ? null : line.TreatmentReason.Trim()));
        }
        if (requestErrors.Count > 0) return Results.ValidationProblem(requestErrors);

        return await runner.ExecuteAsync<IResult>(async (db, token) =>
        {
            if (!await db.Companies.AsNoTracking().AnyAsync(
                    x => x.Id == companyId && x.TenantId == tenant.TenantId, token))
                return Results.NotFound(new { error = "company_not_found" });

            var header = await db.Database.SqlQuery<PurchaseDraftStatusRow>($"""
                SELECT status AS "Status", tax_calculation_status AS "TaxCalculationStatus"
                  FROM purchase_invoice_drafts
                 WHERE tenant_id = {tenant.TenantId} AND company_id = {companyId} AND id = {invoiceId}
                """).SingleOrDefaultAsync(token);
            if (header is null)
                return Results.NotFound(new { error = "purchase_invoice_draft_not_found" });
            if (!header.Status.Equals("DRAFT", StringComparison.OrdinalIgnoreCase))
                return Results.Conflict(new { error = "purchase_draft_not_editable" });

            var storedLines = await db.Database.SqlQuery<PurchaseDraftVatSourceLine>($"""
                SELECT id AS "Id", line_number AS "LineNumber", description AS "Description",
                       quantity AS "Quantity", unit_price AS "UnitPrice"
                  FROM purchase_invoice_draft_lines
                 WHERE tenant_id = {tenant.TenantId} AND company_id = {companyId}
                   AND purchase_invoice_draft_id = {invoiceId}
                 ORDER BY line_number
                """).ToListAsync(token);
            if (storedLines.Count != normalized.Count ||
                !storedLines.Select(line => line.LineNumber).SequenceEqual(normalized.OrderBy(x => x.LineNumber).Select(x => x.LineNumber)))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["lines"] = ["يجب إرسال تصنيف وخصم واحد لكل بند محفوظ دون إضافة أو حذف بنود."]
                });

            var requestedByNumber = normalized.ToDictionary(line => line.LineNumber);
            var calculatorInput = storedLines.Select(line =>
            {
                var requestLine = requestedByNumber[line.LineNumber];
                return new SalesVatLineInput(line.Description, line.Quantity, line.UnitPrice,
                    requestLine.DiscountAmount, requestLine.Treatment, requestLine.TreatmentReason);
            }).ToList();

            SaudiVatCalculation calculation;
            try
            {
                calculation = SaudiVatCalculator.Calculate(calculatorInput);
            }
            catch (ArgumentException ex)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["vatCalculation"] = [ex.Message]
                });
            }
            catch (OverflowException)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["vatCalculation"] = ["إجمالي المبالغ يتجاوز الحد المدعوم."]
                });
            }

            for (var index = 0; index < storedLines.Count; index++)
            {
                var saved = storedLines[index];
                var computed = calculation.Lines[index];
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE purchase_invoice_draft_lines
                       SET discount_amount = {computed.DiscountAmount},
                           vat_treatment = {computed.Treatment.ToString()},
                           vat_treatment_reason = {computed.TreatmentReason},
                           gross_amount = {computed.GrossAmount},
                           net_amount = {computed.NetAmount},
                           vat_rate_percent = {computed.VatRatePercent},
                           vat_amount = {computed.VatAmount},
                           total_amount = {computed.TotalAmount}
                     WHERE tenant_id = {tenant.TenantId} AND company_id = {companyId}
                       AND purchase_invoice_draft_id = {invoiceId} AND line_number = {saved.LineNumber}
                    """, token);
            }

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE purchase_invoice_drafts
                   SET tax_calculation_status = 'CALCULATED',
                       gross_amount = {calculation.GrossAmount},
                       discount_amount = {calculation.DiscountAmount},
                       net_amount = {calculation.NetAmount},
                       vat_amount = {calculation.VatAmount},
                       total_amount = {calculation.TotalAmount}
                 WHERE tenant_id = {tenant.TenantId} AND company_id = {companyId} AND id = {invoiceId}
                """, token);

            db.AuditLogs.Add(new AuditLogRecord
            {
                Id = Guid.NewGuid(), TenantId = tenant.TenantId, UserId = tenant.UserId,
                Action = "PURCHASE_DRAFT_VAT_CALCULATED", EntityType = "PurchaseInvoiceDraft",
                EntityId = invoiceId.ToString("D"), IpAddress = http.Connection.RemoteIpAddress?.ToString(),
                DetailsJson = JsonSerializer.Serialize(new
                {
                    calculation.GrossAmount, calculation.DiscountAmount, calculation.NetAmount,
                    calculation.VatAmount, calculation.TotalAmount, LineCount = calculation.Lines.Count
                }),
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync(token);

            var lines = calculation.Lines.Select((line, index) => new PurchaseDraftVatLineDto(
                storedLines[index].LineNumber, line.Description, line.Treatment.ToString(),
                line.GrossAmount, line.DiscountAmount, line.NetAmount, line.VatRatePercent,
                line.VatAmount, line.TotalAmount, line.TreatmentReason)).ToList();

            return Results.Ok(new PurchaseDraftVatResult(
                invoiceId, companyId, "CALCULATED",
                "تم حفظ حساب VAT الداخلي على مسودة المشتريات. يلزم فحص فاتورة المورد وقابلية استرداد ضريبة المدخلات؛ لم يُنشأ قيد محاسبي ولم تُصدر فاتورة.",
                calculation.GrossAmount, calculation.DiscountAmount, calculation.NetAmount,
                calculation.VatAmount, calculation.TotalAmount, lines));
        }, ct);
    }

    private static bool TryParseTreatment(string? value, out SalesVatTreatment treatment)
    {
        var token = (value ?? "").Trim().Replace("_", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        switch (token)
        {
            case "STANDARD":
            case "STANDARDRATED": treatment = SalesVatTreatment.StandardRated; return true;
            case "ZERO":
            case "ZERORATED": treatment = SalesVatTreatment.ZeroRated; return true;
            case "EXEMPT": treatment = SalesVatTreatment.Exempt; return true;
            case "OUTSIDEOFSCOPE": treatment = SalesVatTreatment.OutsideScope; return true;
            default: treatment = default; return false;
        }
    }

    public sealed record ApplyPurchaseDraftVatRequest(IReadOnlyList<ApplyPurchaseDraftVatLineRequest?>? Lines);
    public sealed record ApplyPurchaseDraftVatLineRequest(int LineNumber, decimal DiscountAmount, string? Treatment, string? TreatmentReason = null);
    public sealed record PurchaseDraftVatLineDto(
        int LineNumber, string Description, string Treatment, decimal GrossAmount, decimal DiscountAmount,
        decimal NetAmount, decimal VatRatePercent, decimal VatAmount, decimal TotalAmount, string? TreatmentReason);
    public sealed record PurchaseDraftVatResult(
        Guid InvoiceId, Guid CompanyId, string TaxCalculationStatus, string Message,
        decimal GrossAmount, decimal DiscountAmount, decimal NetAmount, decimal VatAmount, decimal TotalAmount,
        IReadOnlyList<PurchaseDraftVatLineDto> Lines);
    private sealed record RequestedVatLine(int LineNumber, decimal DiscountAmount, SalesVatTreatment Treatment, string? TreatmentReason);
    private sealed record PurchaseDraftStatusRow(string Status, string TaxCalculationStatus);
    private sealed record PurchaseDraftVatSourceLine(Guid Id, int LineNumber, string Description, decimal Quantity, decimal UnitPrice);
}
