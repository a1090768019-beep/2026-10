using System.Text.Json;
using BusinessERP.Application.Abstractions;
using BusinessERP.Infrastructure.Persistence;
using BusinessERP.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace BusinessERP.Api.Endpoints;

/// <summary>
/// Read/create operations for internal purchase invoice drafts. Supplier source documents must be checked
/// before posting any accounting entry or treating input VAT as recoverable.
/// </summary>
public static class PurchaseInvoiceDraftEndpoints
{
    private static readonly string[] AccountingRoles = ["OWNER", "ADMIN", "ACCOUNTANT"];
    private const decimal MaxAmount = 9_999_999_999_999_999.9999m;

    public static IEndpointRouteBuilder MapPurchaseInvoiceDraftEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1/companies/{companyId:guid}/purchase-invoice-drafts")
            .WithTags("Purchases - Drafts");
        api.MapGet("", ListDrafts).WithName("ListPurchaseInvoiceDrafts");
        api.MapPost("", CreateDraft).WithName("CreatePurchaseInvoiceDraft");
        api.MapGet("/{invoiceId:guid}", GetDraft).WithName("GetPurchaseInvoiceDraft");
        return endpoints;
    }

    private static async Task<IResult> ListDrafts(
        Guid companyId, ITenantContext tenant, TenantTransactionRunner runner, CancellationToken ct) =>
        await runner.ExecuteAsync<IResult>(async (db, token) =>
        {
            if (!await db.Companies.AsNoTracking().AnyAsync(
                    x => x.Id == companyId && x.TenantId == tenant.TenantId, token))
                return Results.NotFound(new { error = "company_not_found" });

            var rows = await db.Database.SqlQuery<PurchaseDraftListRow>($"""
                SELECT draft.id AS "Id", draft.purchase_number AS "PurchaseNumber",
                       draft.supplier_invoice_number AS "SupplierInvoiceNumber",
                       draft.issue_date AS "IssueDate", draft.due_date AS "DueDate",
                       draft.currency_code AS "CurrencyCode", draft.supplier_id AS "SupplierId",
                       draft.supplier_name_snapshot AS "SupplierName",
                       draft.supplier_tax_number_snapshot AS "SupplierTaxNumber",
                       draft.subtotal_amount AS "SubtotalAmount",
                       draft.tax_calculation_status AS "TaxCalculationStatus",
                       draft.gross_amount AS "GrossAmount", draft.discount_amount AS "DiscountAmount",
                       draft.net_amount AS "NetAmount", draft.vat_amount AS "VatAmount",
                       draft.total_amount AS "TotalAmount", draft.status AS "Status",
                       draft.created_at AS "CreatedAt",
                       (SELECT count(*)::integer FROM purchase_invoice_draft_lines line
                         WHERE line.tenant_id = draft.tenant_id AND line.company_id = draft.company_id
                           AND line.purchase_invoice_draft_id = draft.id) AS "LineCount"
                  FROM purchase_invoice_drafts draft
                 WHERE draft.tenant_id = {tenant.TenantId} AND draft.company_id = {companyId}
                 ORDER BY draft.issue_date DESC, draft.created_at DESC
                 LIMIT 500
                """).ToListAsync(token);
            return Results.Ok(rows);
        }, ct);

    private static async Task<IResult> GetDraft(
        Guid companyId, Guid invoiceId, ITenantContext tenant,
        TenantTransactionRunner runner, CancellationToken ct) =>
        await runner.ExecuteAsync<IResult>(async (db, token) =>
        {
            if (!await db.Companies.AsNoTracking().AnyAsync(
                    x => x.Id == companyId && x.TenantId == tenant.TenantId, token))
                return Results.NotFound(new { error = "company_not_found" });

            var draft = await LoadDraftAsync(db, tenant.TenantId, companyId, invoiceId, token);
            return draft is null
                ? Results.NotFound(new { error = "purchase_invoice_draft_not_found" })
                : Results.Ok(draft);
        }, ct);

    private static async Task<IResult> CreateDraft(
        Guid companyId, CreatePurchaseDraftRequest request, ITenantContext tenant,
        HttpContext http, TenantTransactionRunner runner, CancellationToken ct)
    {
        if (!AccountingRoles.Contains(tenant.RoleCode, StringComparer.OrdinalIgnoreCase))
            return Results.Forbid();

        var purchaseNumber = NormalizeDocumentNumber(request.PurchaseNumber);
        var supplierInvoiceNumber = NormalizeDocumentNumber(request.SupplierInvoiceNumber);
        var notes = (request.Notes ?? "").Trim();
        var errors = new Dictionary<string, string[]>();
        if (!IsValidDocumentNumber(purchaseNumber))
            errors["purchaseNumber"] = ["رقم مستند المشتريات مطلوب ويقبل الأحرف اللاتينية والأرقام والشرطة والنقطة والشرطة السفلية والشرطة المائلة."];
        if (!IsValidDocumentNumber(supplierInvoiceNumber))
            errors["supplierInvoiceNumber"] = ["رقم فاتورة المورد مطلوب ويقبل الأحرف اللاتينية والأرقام وعلامات الفصل المسموحة."];
        if (request.SupplierId == Guid.Empty) errors["supplierId"] = ["المورد مطلوب."];
        if (request.IssueDate == default) errors["issueDate"] = ["تاريخ الفاتورة مطلوب."];
        if (request.DueDate.HasValue && request.DueDate.Value < request.IssueDate)
            errors["dueDate"] = ["تاريخ الاستحقاق لا يمكن أن يسبق تاريخ الفاتورة."];
        if (notes.Length > 2000) errors["notes"] = ["الملاحظات لا تتجاوز 2000 حرف."];
        if (request.Lines is null || request.Lines.Count is < 1 or > 500)
            errors["lines"] = ["يجب أن يحتوي المستند على 1 إلى 500 بند."];

        var lines = new List<NormalizedLine>();
        decimal subtotal = 0m;
        if (request.Lines is { Count: > 0 and <= 500 })
        {
            for (var index = 0; index < request.Lines.Count; index++)
            {
                var input = request.Lines[index];
                var key = $"lines[{index}]";
                if (input is null)
                {
                    errors[key] = ["البند مطلوب."];
                    continue;
                }

                var description = (input.Description ?? "").Trim();
                if (description.Length is < 1 or > 500 || description.Any(char.IsControl))
                    errors[$"{key}.description"] = ["وصف البند مطلوب ولا يتجاوز 500 حرف."];
                if (input.Quantity <= 0m || !HasSupportedPrecision(input.Quantity))
                    errors[$"{key}.quantity"] = ["الكمية يجب أن تكون موجبة وبحد أقصى أربع منازل عشرية."];
                if (input.UnitPrice < 0m || !HasSupportedPrecision(input.UnitPrice))
                    errors[$"{key}.unitPrice"] = ["سعر الوحدة يجب ألا يكون سالبًا وبحد أقصى أربع منازل عشرية."];

                if (description.Length is < 1 or > 500 || description.Any(char.IsControl) ||
                    input.Quantity <= 0m || input.UnitPrice < 0m ||
                    !HasSupportedPrecision(input.Quantity) || !HasSupportedPrecision(input.UnitPrice))
                    continue;

                if (input.UnitPrice > 0m && input.Quantity > MaxAmount / input.UnitPrice)
                {
                    errors[$"{key}.lineAmount"] = ["قيمة البند تتجاوز الحد المدعوم."];
                    continue;
                }

                var lineAmount = decimal.Round(input.Quantity * input.UnitPrice, 4, MidpointRounding.AwayFromZero);
                if (lineAmount > MaxAmount || subtotal > MaxAmount - lineAmount)
                {
                    errors[$"{key}.lineAmount"] = ["قيمة البند أو الإجمالي يتجاوز الحد المدعوم."];
                    continue;
                }

                subtotal = checked(subtotal + lineAmount);
                lines.Add(new NormalizedLine(description, input.Quantity, input.UnitPrice, lineAmount));
            }
        }
        if (lines.Count != (request.Lines?.Count ?? 0))
            errors.TryAdd("lines", ["أحد البنود غير صالح."]);
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        return await runner.ExecuteAsync<IResult>(async (db, token) =>
        {
            var company = await db.Companies.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == companyId && x.TenantId == tenant.TenantId, token);
            if (company is null) return Results.NotFound(new { error = "company_not_found" });

            var supplier = await db.Database.SqlQuery<SupplierSnapshot>($"""
                SELECT id AS "Id", name AS "Name", tax_number AS "TaxNumber", is_active AS "IsActive"
                  FROM suppliers
                 WHERE tenant_id = {tenant.TenantId} AND company_id = {companyId} AND id = {request.SupplierId}
                 LIMIT 1
                """).SingleOrDefaultAsync(token);
            if (supplier is null || !supplier.IsActive)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["supplierId"] = ["المورد غير موجود أو غير نشط في هذه الشركة."]
                });

            var numberLock = $"purchase-number:{tenant.TenantId:D}:{companyId:D}:{purchaseNumber}";
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({numberLock}, 0))", token);
            var documentExists = await db.Database.SqlQuery<int>($"""
                SELECT count(*)::integer AS "Value" FROM purchase_invoice_drafts
                 WHERE tenant_id = {tenant.TenantId} AND company_id = {companyId} AND purchase_number = {purchaseNumber}
                """).SingleAsync(token);
            if (documentExists != 0)
                return Results.Conflict(new { error = "purchase_number_conflict", message = "رقم مستند المشتريات مستخدم بالفعل." });

            var supplierInvoiceLock = $"supplier-invoice:{tenant.TenantId:D}:{companyId:D}:{request.SupplierId:D}:{supplierInvoiceNumber}";
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({supplierInvoiceLock}, 0))", token);
            var supplierInvoiceExists = await db.Database.SqlQuery<int>($"""
                SELECT count(*)::integer AS "Value" FROM purchase_invoice_drafts
                 WHERE tenant_id = {tenant.TenantId} AND company_id = {companyId}
                   AND supplier_id = {request.SupplierId} AND supplier_invoice_number = {supplierInvoiceNumber}
                """).SingleAsync(token);
            if (supplierInvoiceExists != 0)
                return Results.Conflict(new
                {
                    error = "supplier_invoice_number_conflict",
                    message = "رقم فاتورة المورد مسجل مسبقًا لهذا المورد."
                });

            var draftId = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO purchase_invoice_drafts
                    (id, tenant_id, company_id, supplier_id, purchase_number, supplier_invoice_number,
                     issue_date, due_date, currency_code, supplier_name_snapshot, supplier_tax_number_snapshot,
                     subtotal_amount, notes, status, created_by)
                VALUES
                    ({draftId}, {tenant.TenantId}, {companyId}, {request.SupplierId}, {purchaseNumber}, {supplierInvoiceNumber},
                     {request.IssueDate}, {request.DueDate}, {company.BaseCurrency.Trim().ToUpperInvariant()},
                     {supplier.Name}, {supplier.TaxNumber}, {subtotal}, {notes}, 'DRAFT', {tenant.UserId})
                """, token);

            for (var index = 0; index < lines.Count; index++)
            {
                var line = lines[index];
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO purchase_invoice_draft_lines
                        (tenant_id, company_id, purchase_invoice_draft_id, line_number, description, quantity, unit_price, line_amount)
                    VALUES
                        ({tenant.TenantId}, {companyId}, {draftId}, {index + 1}, {line.Description}, {line.Quantity}, {line.UnitPrice}, {line.LineAmount})
                    """, token);
            }

            db.AuditLogs.Add(new AuditLogRecord
            {
                Id = Guid.NewGuid(), TenantId = tenant.TenantId, UserId = tenant.UserId,
                Action = "PURCHASE_INVOICE_DRAFT_CREATED", EntityType = "PurchaseInvoiceDraft",
                EntityId = draftId.ToString("D"), IpAddress = http.Connection.RemoteIpAddress?.ToString(),
                DetailsJson = JsonSerializer.Serialize(new
                {
                    PurchaseNumber = purchaseNumber, SupplierInvoiceNumber = supplierInvoiceNumber,
                    SupplierId = supplier.Id, IssueDate = request.IssueDate, Subtotal = subtotal, LineCount = lines.Count
                }),
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync(token);

            var created = await LoadDraftAsync(db, tenant.TenantId, companyId, draftId, token);
            return Results.Created($"/api/v1/companies/{companyId}/purchase-invoice-drafts/{draftId}", created);
        }, ct);
    }

    private static async Task<PurchaseDraftDetails?> LoadDraftAsync(
        ErpDbContext db, Guid tenantId, Guid companyId, Guid invoiceId, CancellationToken token)
    {
        var header = await db.Database.SqlQuery<PurchaseDraftHeader>($"""
            SELECT id AS "Id", company_id AS "CompanyId", supplier_id AS "SupplierId",
                   purchase_number AS "PurchaseNumber", supplier_invoice_number AS "SupplierInvoiceNumber",
                   issue_date AS "IssueDate", due_date AS "DueDate", currency_code AS "CurrencyCode",
                   supplier_name_snapshot AS "SupplierName", supplier_tax_number_snapshot AS "SupplierTaxNumber",
                   subtotal_amount AS "SubtotalAmount", notes AS "Notes", status AS "Status",
                   tax_calculation_status AS "TaxCalculationStatus", gross_amount AS "GrossAmount",
                   discount_amount AS "DiscountAmount", net_amount AS "NetAmount", vat_amount AS "VatAmount",
                   total_amount AS "TotalAmount", created_at AS "CreatedAt"
              FROM purchase_invoice_drafts
             WHERE tenant_id = {tenantId} AND company_id = {companyId} AND id = {invoiceId}
            """).SingleOrDefaultAsync(token);
        if (header is null) return null;

        var lines = await db.Database.SqlQuery<PurchaseDraftLineDto>($"""
            SELECT id AS "Id", line_number AS "LineNumber", description AS "Description",
                   quantity AS "Quantity", unit_price AS "UnitPrice", line_amount AS "LineAmount",
                   discount_amount AS "DiscountAmount", vat_treatment AS "VatTreatment",
                   vat_treatment_reason AS "VatTreatmentReason", gross_amount AS "GrossAmount",
                   net_amount AS "NetAmount", vat_rate_percent AS "VatRatePercent",
                   vat_amount AS "VatAmount", total_amount AS "TotalAmount"
              FROM purchase_invoice_draft_lines
             WHERE tenant_id = {tenantId} AND company_id = {companyId} AND purchase_invoice_draft_id = {invoiceId}
             ORDER BY line_number
            """).ToListAsync(token);

        return new PurchaseDraftDetails(
            header.Id, header.CompanyId, header.SupplierId, header.PurchaseNumber, header.SupplierInvoiceNumber,
            header.IssueDate, header.DueDate, header.CurrencyCode, header.SupplierName, header.SupplierTaxNumber,
            header.SubtotalAmount, header.Notes, header.Status, header.TaxCalculationStatus, header.GrossAmount,
            header.DiscountAmount, header.NetAmount, header.VatAmount, header.TotalAmount, header.CreatedAt, lines);
    }

    private static string NormalizeDocumentNumber(string? value) => (value ?? "").Trim().ToUpperInvariant();
    private static bool IsValidDocumentNumber(string value) =>
        value.Length is >= 1 and <= 80 &&
        value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '/' or '.');
    private static bool HasSupportedPrecision(decimal value) =>
        decimal.Round(value, 4, MidpointRounding.ToEven) == value;

    public sealed record CreatePurchaseDraftRequest(
        Guid SupplierId, string? PurchaseNumber, string? SupplierInvoiceNumber,
        DateOnly IssueDate, DateOnly? DueDate, string? Notes,
        IReadOnlyList<CreatePurchaseDraftLineRequest?>? Lines);
    public sealed record CreatePurchaseDraftLineRequest(string? Description, decimal Quantity, decimal UnitPrice);
    private sealed record NormalizedLine(string Description, decimal Quantity, decimal UnitPrice, decimal LineAmount);
    private sealed record SupplierSnapshot(Guid Id, string Name, string? TaxNumber, bool IsActive);
    private sealed record PurchaseDraftHeader(
        Guid Id, Guid CompanyId, Guid SupplierId, string PurchaseNumber, string SupplierInvoiceNumber,
        DateOnly IssueDate, DateOnly? DueDate, string CurrencyCode, string SupplierName, string? SupplierTaxNumber,
        decimal SubtotalAmount, string Notes, string Status, string TaxCalculationStatus,
        decimal? GrossAmount, decimal? DiscountAmount, decimal? NetAmount, decimal? VatAmount, decimal? TotalAmount, DateTime CreatedAt);
    public sealed record PurchaseDraftLineDto(
        Guid Id, int LineNumber, string Description, decimal Quantity, decimal UnitPrice, decimal LineAmount,
        decimal DiscountAmount, string VatTreatment, string? VatTreatmentReason, decimal? GrossAmount,
        decimal? NetAmount, decimal? VatRatePercent, decimal? VatAmount, decimal? TotalAmount);
    public sealed record PurchaseDraftDetails(
        Guid Id, Guid CompanyId, Guid SupplierId, string PurchaseNumber, string SupplierInvoiceNumber,
        DateOnly IssueDate, DateOnly? DueDate, string CurrencyCode, string SupplierName, string? SupplierTaxNumber,
        decimal SubtotalAmount, string Notes, string Status, string TaxCalculationStatus,
        decimal? GrossAmount, decimal? DiscountAmount, decimal? NetAmount, decimal? VatAmount, decimal? TotalAmount,
        DateTime CreatedAt, IReadOnlyList<PurchaseDraftLineDto> Lines);
    public sealed record PurchaseDraftListRow(
        Guid Id, string PurchaseNumber, string SupplierInvoiceNumber, DateOnly IssueDate, DateOnly? DueDate,
        string CurrencyCode, Guid SupplierId, string SupplierName, string? SupplierTaxNumber,
        decimal SubtotalAmount, string TaxCalculationStatus, decimal? GrossAmount, decimal? DiscountAmount,
        decimal? NetAmount, decimal? VatAmount, decimal? TotalAmount, string Status, DateTime CreatedAt, int LineCount);
}
