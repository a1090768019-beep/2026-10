using BusinessERP.Application.Abstractions;
using BusinessERP.Domain.Tax;
using BusinessERP.Infrastructure.Persistence;
using BusinessERP.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace BusinessERP.Api.Endpoints;

/// <summary>
/// Calculates an ephemeral VAT preview. The response is not stored and is not a tax invoice,
/// ZATCA-compliant XML, cleared invoice, or accounting posting.
/// </summary>
public static class SalesVatPreviewEndpoints
{
    private static readonly string[] AllowedRoles = ["OWNER", "ADMIN", "ACCOUNTANT", "SALES"];

    public static IEndpointRouteBuilder MapSalesVatPreviewEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1/companies/{companyId:guid}/sales-vat-preview")
            .WithTags("Sales - VAT preview");
        api.MapPost("", CalculatePreview).WithName("CalculateSalesVatPreview");
        return endpoints;
    }

    private static async Task<IResult> CalculatePreview(
        Guid companyId,
        CalculateSalesVatPreviewRequest request,
        ITenantContext tenant,
        TenantTransactionRunner runner,
        CancellationToken ct)
    {
        if (!AllowedRoles.Contains(tenant.RoleCode, StringComparer.OrdinalIgnoreCase))
            return Results.Forbid();

        var requestLines = request.Lines;
        if (requestLines is null || requestLines.Count is < 1 or > SaudiVatCalculator.MaximumLines)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["lines"] = [$"يجب إدخال بند واحد إلى {SaudiVatCalculator.MaximumLines} بندًا."]
            });

        var mapped = new List<SalesVatLineInput>(requestLines.Count);
        var errors = new Dictionary<string, string[]>();
        for (var index = 0; index < requestLines.Count; index++)
        {
            var line = requestLines[index];
            if (line is null)
            {
                errors[$"lines[{index}]"] = ["البند مطلوب."];
                continue;
            }

            if (!TryParseTreatment(line.Treatment, out var treatment))
            {
                errors[$"lines[{index}].treatment"] = ["تصنيف الضريبة غير معروف. استخدم StandardRated أو ZeroRated أو Exempt أو OutsideScope."];
                continue;
            }

            mapped.Add(new SalesVatLineInput(
                line.Description ?? "",
                line.Quantity,
                line.UnitPrice,
                line.DiscountAmount,
                treatment,
                line.TreatmentReason));
        }

        if (errors.Count > 0)
            return Results.ValidationProblem(errors);

        return await runner.ExecuteAsync<IResult>(async (db, token) =>
        {
            var company = await db.Companies.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == companyId, token);
            if (company is null)
                return Results.NotFound(new { error = "company_not_found" });

            SaudiVatCalculation calculation;
            try
            {
                calculation = SaudiVatCalculator.Calculate(mapped);
            }
            catch (ArgumentException ex)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["calculation"] = [ex.Message]
                });
            }

            var lines = calculation.Lines.Select((line, index) => new SalesVatPreviewLineDto(
                index + 1,
                line.Description,
                line.Treatment.ToString(),
                line.GrossAmount,
                line.DiscountAmount,
                line.NetAmount,
                line.VatRatePercent,
                line.VatAmount,
                line.TotalAmount,
                line.TreatmentReason)).ToList();

            return Results.Ok(new SalesVatPreviewDto(
                company.Id,
                company.BaseCurrency.Trim(),
                "PREVIEW_ONLY",
                "هذه معاينة داخلية غير محفوظة؛ ليست فاتورة ضريبية ولا تعني توافقًا مع ZATCA.",
                calculation.GrossAmount,
                calculation.DiscountAmount,
                calculation.NetAmount,
                calculation.VatAmount,
                calculation.TotalAmount,
                lines));
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

    public sealed record CalculateSalesVatPreviewRequest(IReadOnlyList<SalesVatPreviewLineRequest?>? Lines);
    public sealed record SalesVatPreviewLineRequest(
        string? Description,
        decimal Quantity,
        decimal UnitPrice,
        decimal DiscountAmount = 0m,
        string? Treatment = null,
        string? TreatmentReason = null);
    public sealed record SalesVatPreviewLineDto(
        int LineNumber,
        string Description,
        string Treatment,
        decimal GrossAmount,
        decimal DiscountAmount,
        decimal NetAmount,
        decimal VatRatePercent,
        decimal VatAmount,
        decimal TotalAmount,
        string? TreatmentReason);
    public sealed record SalesVatPreviewDto(
        Guid CompanyId,
        string CurrencyCode,
        string DocumentStatus,
        string Message,
        decimal GrossAmount,
        decimal DiscountAmount,
        decimal NetAmount,
        decimal VatAmount,
        decimal TotalAmount,
        IReadOnlyList<SalesVatPreviewLineDto> Lines);
}
