using System.Text.Json;
using BusinessERP.Application.Abstractions;
using BusinessERP.Domain.Tax;
using BusinessERP.Infrastructure.Persistence;
using BusinessERP.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace BusinessERP.Api.Endpoints;

/// <summary>
/// Persists a reviewed internal VAT calculation on a saved sales draft in a single PostgreSQL transaction.
/// This does not finalize or issue the draft as a tax invoice and does not post a journal entry.
/// </summary>
public static class SalesDraftVatEndpoints
{
    private static readonly string[] AllowedRoles = ["OWNER", "ADMIN", "ACCOUNTANT"];

    public static IEndpointRouteBuilder MapSalesDraftVatEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
                "/api/v1/companies/{companyId:guid}/sales-invoice-drafts/{invoiceId:guid}/calculate-vat",
                CalculateAndPersistVat)
            .WithName("CalculateAndPersistSalesDraftVat")
            .WithTags("Sales - Draft VAT");
        return endpoints;
    }

    private static async Task<IResult> CalculateAndPersistVat(
        Guid companyId, Guid invoiceId, ApplySalesDraftVatRequest request,
        ITenantContext tenant, HttpContext http, TenantTransactionRunner runner, CancellationToken ct)
    {
        if (!AllowedRoles.Contains(tenant.RoleCode, StringComparer.OrdinalIgnoreCase))
            return Results.Forbid();

        if (request.Lines is null || request.Lines.Count is < 1 or > SaudiVatCalculator.MaximumLines)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["lines"] = [$"يجب تصنيف من بند واحد إلى {SaudiVatCalculator.MaximumLines} بندًا."]
            });

        if (request.Lines.Any(x => x is null) ||
            request.Lines.Select(x => x!.LineNumber).Distinct().Count() != request.Lines.Count ||
            request.Lines.Any(x => x!.LineNumber < 1))
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["lines"] = ["يجب أن يكون لكل بند رقم فريد وصحيح."]
            });

        return await runner.ExecuteAsync<IResult>(async (db, token) =>
        {
            if (!await db.Companies.AsNoTracking().AnyAsync(
                    x => x.Id == companyId && x.TenantId == tenant.TenantId, token))
                return Results.NotFound(new { error = "company_not_found" });

            var draft = await db.Database.SqlQuery<DraftStatusRow>($"""
                SELECT status AS "Status"
                  FROM sales_invoice_drafts
                 WHERE tenant_id = {tenant.TenantId}
                   AND company_id = {companyId}
                   AND id = {invoiceId}
                 LIMIT 1
                """).SingleOrDefaultAsync(token);

            if (draft is null)
                return Results.NotFound(new { error = "sales_invoice_draft_not_found" });
            if (!string.Equals(draft.Status, "DRAFT", StringComparison.Ordinal))
                return Results.Conflict(new { error = "sales_invoice_draft_not_editable" });

            var storedLines = await db.Database.SqlQuery<DraftLineSource>($"""
                SELECT id AS "Id",
                       line_number AS "LineNumber",
                       description AS "Description",
                       quantity AS "Quantity",
                       unit_price AS "UnitPrice"
                  FROM sales_invoice_draft_lines
                 WHERE tenant_id = {tenant.TenantId}
                   AND company_id = {companyId}
                   AND sales_invoice_draft_id = {invoiceId}
                 ORDER BY line_number
                """).ToListAsync(token);

            if (storedLines.Count != request.Lines.Count ||
                storedLines.Any(saved => !request.Lines.Any(input => input!.LineNumber == saved.LineNumber)))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["lines"] = ["يجب أن يحتوي الطلب على تصنيف واحد لكل بند محفوظ في المسودة."]
                });

            var treatmentByLine = new Dictionary<int, ApplySalesDraftVatLineRequest>();
            var errors = new Dictionary<string, string[]>();
            foreach (var line in request.Lines.Select(x => x!))
            {
                if (!TryParseTreatment(line.Treatment, out _))
                    errors[$"lines[{line.LineNumber}].treatment"] = ["تصنيف الضريبة غير معروف."];
                if (line.DiscountAmount < 0m ||
                    decimal.Round(line.DiscountAmount, SaudiVatCalculator.MoneyScale, MidpointRounding.ToEven) != line.DiscountAmount)
                    errors[$"lines[{line.LineNumber}].discountAmount"] = ["الخصم يجب أن يكون غير سالب وبحد أقصى منزلتين عشريتين."];
                treatmentByLine.Add(line.LineNumber, line);
            }
            if (errors.Count > 0)
                return Results.ValidationProblem(errors);

            var vatInputs = new List<SalesVatLineInput>(storedLines.Count);
            foreach (var saved in storedLines)
            {
                var input = treatmentByLine[saved.LineNumber];
                if (!TryParseTreatment(input.Treatment, out var treatment))
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["treatment"] = ["تصنيف الضريبة غير معروف."] });

                vatInputs.Add(new SalesVatLineInput(
                    saved.Description, saved.Quantity, saved.UnitPrice,
                    input.DiscountAmount, treatment, input.TreatmentReason));
            }

            SaudiVatCalculation calculation;
            try
            {
                calculation = SaudiVatCalculator.Calculate(vatInputs);
            }
            catch (ArgumentException ex)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["taxCalculation"] = [ex.Message] });
            }
            catch (OverflowException)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["taxCalculation"] = ["إجمالي ضريبة المسودة يتجاوز الحد المدعوم."]
                });
            }

            for (var index = 0; index < storedLines.Count; index++)
            {
                var saved = storedLines[index];
                var calculated = calculation.Lines[index];
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE sales_invoice_draft_lines
                       SET discount_amount = {calculated.DiscountAmount},
                           vat_treatment = {calculated.Treatment.ToString()},
                           vat_treatment_reason = {calculated.TreatmentReason},
                           gross_amount = {calculated.GrossAmount},
                           net_amount = {calculated.NetAmount},
                           vat_rate_percent = {calculated.VatRatePercent},
                           vat_amount = {calculated.VatAmount},
                           total_amount = {calculated.TotalAmount}
                     WHERE tenant_id = {tenant.TenantId}
                       AND company_id = {companyId}
                       AND sales_invoice_draft_id = {invoiceId}
                       AND line_number = {saved.LineNumber}
                    """, token);
            }

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE sales_invoice_drafts
                   SET tax_calculation_status = 'CALCULATED',
                       gross_amount = {calculation.GrossAmount},
                       discount_amount = {calculation.DiscountAmount},
                       net_amount = {calculation.NetAmount},
                       vat_amount = {calculation.VatAmount},
                       total_amount = {calculation.TotalAmount}
                 WHERE tenant_id = {tenant.TenantId}
                   AND company_id = {companyId}
                   AND id = {invoiceId}
                """, token);

            db.AuditLogs.Add(new AuditLogRecord
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.TenantId,
                UserId = tenant.UserId,
                Action = "SALES_DRAFT_VAT_CALCULATED",
                EntityType = "SalesInvoiceDraft",
                EntityId = invoiceId.ToString("D"),
                IpAddress = http.Connection.RemoteIpAddress?.ToString(),
                DetailsJson = JsonSerializer.Serialize(new
                {
                    calculation.GrossAmount, calculation.DiscountAmount, calculation.NetAmount,
                    calculation.VatAmount, calculation.TotalAmount,
                    LineCount = calculation.Lines.Count
                }),
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync(token);

            var responseLines = calculation.Lines.Select((line, index) => new PersistedVatLineDto(
                storedLines[index].LineNumber, line.Description, line.Treatment.ToString(),
                line.GrossAmount, line.DiscountAmount, line.NetAmount, line.VatRatePercent,
                line.VatAmount, line.TotalAmount, line.TreatmentReason)).ToList();

            return Results.Ok(new PersistedSalesDraftVatDto(
                invoiceId, companyId, "CALCULATED",
                "تم حفظ حساب VAT على المسودة فقط؛ المستند ليس فاتورة ضريبية نهائية ولا يُرسل إلى ZATCA ولا ينشئ قيدًا محاسبيًا.",
                calculation.GrossAmount, calculation.DiscountAmount, calculation.NetAmount,
                calculation.VatAmount, calculation.TotalAmount, responseLines));
        }, ct);
    }

    private static bool TryParseTreatment(string? value, out SalesVatTreatment treatment)
    {
        var token = (value ?? "").Trim().Replace("_", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal)
            .Replace(" ", "", StringComparison.Ordinal)
            .ToUpperInvariant();
        switch (token)
        {
            case "STANDARD":
            case "STANDARDRATED":
                treatment = SalesVatTreatment.StandardRated;
                return true;
            case "ZERO":
            case "ZERORATED":
                treatment = SalesVatTreatment.ZeroRated;
                return true;
            case "EXEMPT":
                treatment = SalesVatTreatment.Exempt;
                return true;
            case "OUTSIDEOFSCOPE":
                treatment = SalesVatTreatment.OutsideScope;
                return true;
            default:
                treatment = default;
                return false;
        }
    }

    public sealed record ApplySalesDraftVatRequest(IReadOnlyList<ApplySalesDraftVatLineRequest?>? Lines);
    public sealed record ApplySalesDraftVatLineRequest(
        int LineNumber, decimal DiscountAmount, string? Treatment, string? TreatmentReason = null);
    public sealed record PersistedVatLineDto(
        int LineNumber, string Description, string Treatment, decimal GrossAmount, decimal DiscountAmount,
        decimal NetAmount, decimal VatRatePercent, decimal VatAmount, decimal TotalAmount, string? TreatmentReason);
    public sealed record PersistedSalesDraftVatDto(
        Guid InvoiceId, Guid CompanyId, string TaxCalculationStatus, string Message,
        decimal GrossAmount, decimal DiscountAmount, decimal NetAmount, decimal VatAmount, decimal TotalAmount,
        IReadOnlyList<PersistedVatLineDto> Lines);
    public sealed class DraftStatusRow { public string Status { get; init; } = ""; }
    public sealed class DraftLineSource
    {
        public Guid Id { get; init; }
        public int LineNumber { get; init; }
        public string Description { get; init; } = "";
        public decimal Quantity { get; init; }
        public decimal UnitPrice { get; init; }
    }
}
