using System.Text.Json;
using BusinessERP.Application.Abstractions;
using BusinessERP.Infrastructure.Persistence;
using BusinessERP.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace BusinessERP.Api.Endpoints;

/// <summary>
/// Creates and reads internal sales invoice drafts. These are not legally issued tax invoices and
/// deliberately have no VAT, finalization, payment or general-ledger posting workflow yet.
/// </summary>
public static class SalesInvoiceDraftEndpoints
{
    private static readonly string[] AccountingRoles = ["OWNER", "ADMIN", "ACCOUNTANT"];
    private const decimal MaxAmount = 9_999_999_999_999_999.9999m;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapSalesInvoiceDraftEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1/companies/{companyId:guid}/sales-invoice-drafts")
            .WithTags("Sales - Invoice drafts");
        api.MapGet("", ListDrafts).WithName("ListSalesInvoiceDrafts");
        api.MapPost("", CreateDraft).WithName("CreateSalesInvoiceDraft");
        api.MapGet("/{invoiceId:guid}", GetDraft).WithName("GetSalesInvoiceDraft");
        return endpoints;
    }

    private static async Task<IResult> ListDrafts(
        Guid companyId, ITenantContext tenant, TenantTransactionRunner runner, CancellationToken ct) =>
        await runner.ExecuteAsync<IResult>(async (db, token) =>
        {
            if (!await db.Companies.AsNoTracking().AnyAsync(
                    x => x.Id == companyId && x.TenantId == tenant.TenantId, token))
                return Results.NotFound(new { error = "company_not_found" });

            var rows = await db.Database.SqlQuery<DraftListRow>($"""
                SELECT invoice.id AS "Id",
                       invoice.invoice_number AS "InvoiceNumber",
                       invoice.issue_date AS "IssueDate",
                       invoice.due_date AS "DueDate",
                       invoice.currency_code AS "CurrencyCode",
                       invoice.customer_id AS "CustomerId",
                       invoice.customer_name_snapshot AS "CustomerName",
                       invoice.subtotal_amount AS "SubtotalAmount",
                       invoice.status AS "Status",
                       invoice.created_at AS "CreatedAt",
                       (SELECT count(*)::integer
                          FROM sales_invoice_draft_lines line
                         WHERE line.tenant_id = invoice.tenant_id
                           AND line.company_id = invoice.company_id
                           AND line.sales_invoice_draft_id = invoice.id) AS "LineCount"
                  FROM sales_invoice_drafts invoice
                 WHERE invoice.tenant_id = {tenant.TenantId}
                   AND invoice.company_id = {companyId}
                 ORDER BY invoice.issue_date DESC, invoice.created_at DESC
                 LIMIT 500
                """).ToListAsync(token);
            return Results.Ok(rows.Select(ToListDto).ToList());
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
            return draft is null ? Results.NotFound(new { error = "sales_invoice_draft_not_found" }) : Results.Ok(draft);
        }, ct);

    private static async Task<IResult> CreateDraft(
        Guid companyId, CreateDraftRequest request, ITenantContext tenant, HttpContext http,
        TenantTransactionRunner runner, CancellationToken ct)
    {
        if (!AccountingRoles.Contains(tenant.RoleCode, StringComparer.OrdinalIgnoreCase))
            return Results.Forbid();

        var invoiceNumber = (request.InvoiceNumber ?? "").Trim().ToUpperInvariant();
        var notes = (request.Notes ?? "").Trim();
        var errors = new Dictionary<string, string[]>();
        if (invoiceNumber.Length is < 1 or > 80 ||
            invoiceNumber.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '/' or '.')))
            errors["invoiceNumber"] = ["رقم المسودة مطلوب ويقبل الأحرف اللاتينية والأرقام والشرطة والنقطة والشرطة السفلية والشرطة المائلة."];
        if (request.CustomerId == Guid.Empty) errors["customerId"] = ["العميل مطلوب."];
        if (request.IssueDate == default) errors["issueDate"] = ["تاريخ الإصدار مطلوب."];
        if (request.DueDate.HasValue && request.DueDate.Value < request.IssueDate)
            errors["dueDate"] = ["تاريخ الاستحقاق لا يمكن أن يسبق تاريخ الإصدار."];
        if (notes.Length > 2000) errors["notes"] = ["الملاحظات لا تتجاوز 2000 حرف."];
        if (request.Lines is null || request.Lines.Count is < 1 or > 500)
            errors["lines"] = ["يجب أن تحتوي المسودة على 1 إلى 500 بند."];

        var lines = new List<NormalizedLine>();
        decimal subtotal = 0m;
        if (request.Lines is not null && request.Lines.Count is > 0 and <= 500)
        {
            for (var index = 0; index < request.Lines.Count; index++)
            {
                var input = request.Lines[index];
                var description = (input.Description ?? "").Trim();
                var key = $"lines[{index}]";
                if (description.Length is < 1 or > 500)
                    errors[$"{key}.description"] = ["وصف البند مطلوب ولا يتجاوز 500 حرف."];
                if (input.Quantity <= 0m || !HasSupportedPrecision(input.Quantity))
                    errors[$"{key}.quantity"] = ["الكمية يجب أن تكون موجبة وبحد أقصى أربع منازل عشرية."];
                if (input.UnitPrice < 0m || !HasSupportedPrecision(input.UnitPrice))
                    errors[$"{key}.unitPrice"] = ["سعر الوحدة يجب أن يكون غير سالب وبحد أقصى أربع منازل عشرية."];

                if (description.Length is < 1 or > 500 ||
                    input.Quantity <= 0m || input.UnitPrice < 0m ||
                    !HasSupportedPrecision(input.Quantity) || !HasSupportedPrecision(input.UnitPrice))
                    continue;

                if (input.UnitPrice > 0m && input.Quantity > MaxAmount / input.UnitPrice)
                {
                    errors[$"{key}.lineAmount"] = ["قيمة البند تتجاوز الحد المدعوم."];
                    continue;
                }

                var lineAmount = decimal.Round(input.Quantity * input.UnitPrice, 4, MidpointRounding.AwayFromZero);
                if (lineAmount > MaxAmount)
                {
                    errors[$"{key}.lineAmount"] = ["قيمة البند تتجاوز الحد المدعوم."];
                    continue;
                }

                subtotal += lineAmount;
                lines.Add(new NormalizedLine(description, input.Quantity, input.UnitPrice, lineAmount));
            }
        }

        if (subtotal > MaxAmount) errors["subtotal"] = ["إجمالي المسودة يتجاوز الحد المدعوم."];
        if (lines.Count != (request.Lines?.Count ?? 0))
            errors.TryAdd("lines", ["أحد البنود غير صالح."]);
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        return await runner.ExecuteAsync<IResult>(async (db, token) =>
        {
            var company = await db.Companies.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == companyId && x.TenantId == tenant.TenantId, token);
            if (company is null) return Results.NotFound(new { error = "company_not_found" });

            var customer = await db.Database.SqlQuery<CustomerSnapshot>($"""
                SELECT id AS "Id",
                       name AS "Name",
                       tax_number AS "TaxNumber",
                       is_active AS "IsActive"
                  FROM customers
                 WHERE tenant_id = {tenant.TenantId}
                   AND company_id = {companyId}
                   AND id = {request.CustomerId}
                 LIMIT 1
                """).SingleOrDefaultAsync(token);
            if (customer is null || !customer.IsActive)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["customerId"] = ["العميل غير موجود أو غير نشط في هذه الشركة."]
                });

            var invoiceLockKey = "sales-invoice-number:" + tenant.TenantId.ToString("D") + ":" +
                                 companyId.ToString("D") + ":" + invoiceNumber;
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({invoiceLockKey}, 0))", token);

            var exists = await db.Database.SqlQuery<int>($"""
                SELECT count(*)::integer AS "Value"
                  FROM sales_invoice_drafts
                 WHERE tenant_id = {tenant.TenantId}
                   AND company_id = {companyId}
                   AND invoice_number = {invoiceNumber}
                """).SingleAsync(token);
            if (exists != 0)
                return Results.Conflict(new { error = "sales_invoice_number_conflict", message = "رقم المسودة مستخدم بالفعل في هذه الشركة." });

            var draftId = await db.Database.SqlQuery<Guid>($"""
                INSERT INTO sales_invoice_drafts
                    (tenant_id, company_id, customer_id, invoice_number, issue_date, due_date,
                     currency_code, customer_name_snapshot, customer_tax_number_snapshot,
                     subtotal_amount, notes, status, created_by)
                VALUES
                    ({tenant.TenantId}, {companyId}, {customer.Id}, {invoiceNumber}, {request.IssueDate},
                     {request.DueDate}, {company.BaseCurrency.Trim().ToUpperInvariant()},
                     {customer.Name}, {customer.TaxNumber}, {subtotal}, {notes}, 'DRAFT', {tenant.UserId})
                RETURNING id AS "Value"
                """).SingleAsync(token);

            for (var index = 0; index < lines.Count; index++)
            {
                var line = lines[index];
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO sales_invoice_draft_lines
                        (tenant_id, company_id, sales_invoice_draft_id, line_number,
                         description, quantity, unit_price, line_amount)
                    VALUES
                        ({tenant.TenantId}, {companyId}, {draftId}, {index + 1},
                         {line.Description}, {line.Quantity}, {line.UnitPrice}, {line.LineAmount})
                    """, token);
            }

            db.AuditLogs.Add(new AuditLogRecord
            {
                Id = Guid.NewGuid(), TenantId = tenant.TenantId, UserId = tenant.UserId,
                Action = "SALES_INVOICE_DRAFT_CREATED", EntityType = "SalesInvoiceDraft",
                EntityId = draftId.ToString("D"), IpAddress = http.Connection.RemoteIpAddress?.ToString(),
                DetailsJson = JsonSerializer.Serialize(new
                {
                    InvoiceNumber = invoiceNumber, CustomerId = customer.Id,
                    IssueDate = request.IssueDate, Subtotal = subtotal, LineCount = lines.Count
                }, JsonOptions),
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync(token);

            var dto = await LoadDraftAsync(db, tenant.TenantId, companyId, draftId, token);
            return Results.Created(
                $"/api/v1/companies/{companyId}/sales-invoice-drafts/{draftId}", dto);
        }, ct);
    }

    private static async Task<SalesInvoiceDraftDto?> LoadDraftAsync(
        ErpDbContext db, Guid tenantId, Guid companyId, Guid invoiceId, CancellationToken token)
    {
        var header = await db.Database.SqlQuery<DraftHeaderRow>($"""
            SELECT id AS "Id",
                   company_id AS "CompanyId",
                   customer_id AS "CustomerId",
                   invoice_number AS "InvoiceNumber",
                   issue_date AS "IssueDate",
                   due_date AS "DueDate",
                   currency_code AS "CurrencyCode",
                   customer_name_snapshot AS "CustomerName",
                   customer_tax_number_snapshot AS "CustomerTaxNumber",
                   subtotal_amount AS "SubtotalAmount",
                   notes AS "Notes",
                   status AS "Status",
                   created_at AS "CreatedAt"
              FROM sales_invoice_drafts
             WHERE tenant_id = {tenantId}
               AND company_id = {companyId}
               AND id = {invoiceId}
             LIMIT 1
            """).SingleOrDefaultAsync(token);
        if (header is null) return null;

        var lines = await db.Database.SqlQuery<DraftLineRow>($"""
            SELECT id AS "Id",
                   line_number AS "LineNumber",
                   description AS "Description",
                   quantity AS "Quantity",
                   unit_price AS "UnitPrice",
                   line_amount AS "LineAmount"
              FROM sales_invoice_draft_lines
             WHERE tenant_id = {tenantId}
               AND company_id = {companyId}
               AND sales_invoice_draft_id = {invoiceId}
             ORDER BY line_number
            """).ToListAsync(token);

        return new SalesInvoiceDraftDto(
            header.Id, header.CompanyId, header.CustomerId, header.InvoiceNumber,
            header.IssueDate, header.DueDate, header.CurrencyCode.Trim(),
            header.CustomerName, header.CustomerTaxNumber, header.SubtotalAmount,
            header.Notes, header.Status, header.CreatedAt,
            lines.Select(x => new DraftLineDto(x.Id, x.LineNumber, x.Description, x.Quantity, x.UnitPrice, x.LineAmount)).ToList());
    }

    private static DraftListDto ToListDto(DraftListRow row) => new(
        row.Id, row.InvoiceNumber, row.IssueDate, row.DueDate, row.CurrencyCode.Trim(),
        row.CustomerId, row.CustomerName, row.SubtotalAmount, row.Status, row.CreatedAt, row.LineCount);

    private static bool HasSupportedPrecision(decimal value) =>
        value >= 0m && value <= MaxAmount && decimal.Round(value, 4, MidpointRounding.ToEven) == value;

    private sealed record NormalizedLine(string Description, decimal Quantity, decimal UnitPrice, decimal LineAmount);

    public sealed record CreateDraftRequest(
        Guid CustomerId, string? InvoiceNumber, DateOnly IssueDate, DateOnly? DueDate,
        string? Notes, IReadOnlyList<CreateDraftLineRequest>? Lines);
    public sealed record CreateDraftLineRequest(string? Description, decimal Quantity, decimal UnitPrice);
    public sealed record DraftListDto(
        Guid Id, string InvoiceNumber, DateOnly IssueDate, DateOnly? DueDate, string CurrencyCode,
        Guid CustomerId, string CustomerName, decimal SubtotalAmount, string Status, DateTime CreatedAt, int LineCount);
    public sealed record SalesInvoiceDraftDto(
        Guid Id, Guid CompanyId, Guid CustomerId, string InvoiceNumber, DateOnly IssueDate,
        DateOnly? DueDate, string CurrencyCode, string CustomerName, string? CustomerTaxNumber,
        decimal SubtotalAmount, string Notes, string Status, DateTime CreatedAt, IReadOnlyList<DraftLineDto> Lines);
    public sealed record DraftLineDto(Guid Id, int LineNumber, string Description, decimal Quantity, decimal UnitPrice, decimal LineAmount);

    public sealed class CustomerSnapshot
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = "";
        public string? TaxNumber { get; init; }
        public bool IsActive { get; init; }
    }
    public sealed class DraftListRow
    {
        public Guid Id { get; init; }
        public string InvoiceNumber { get; init; } = "";
        public DateOnly IssueDate { get; init; }
        public DateOnly? DueDate { get; init; }
        public string CurrencyCode { get; init; } = "SAR";
        public Guid CustomerId { get; init; }
        public string CustomerName { get; init; } = "";
        public decimal SubtotalAmount { get; init; }
        public string Status { get; init; } = "DRAFT";
        public DateTime CreatedAt { get; init; }
        public int LineCount { get; init; }
    }
    public sealed class DraftHeaderRow
    {
        public Guid Id { get; init; }
        public Guid CompanyId { get; init; }
        public Guid CustomerId { get; init; }
        public string InvoiceNumber { get; init; } = "";
        public DateOnly IssueDate { get; init; }
        public DateOnly? DueDate { get; init; }
        public string CurrencyCode { get; init; } = "SAR";
        public string CustomerName { get; init; } = "";
        public string? CustomerTaxNumber { get; init; }
        public decimal SubtotalAmount { get; init; }
        public string Notes { get; init; } = "";
        public string Status { get; init; } = "DRAFT";
        public DateTime CreatedAt { get; init; }
    }
    public sealed class DraftLineRow
    {
        public Guid Id { get; init; }
        public int LineNumber { get; init; }
        public string Description { get; init; } = "";
        public decimal Quantity { get; init; }
        public decimal UnitPrice { get; init; }
        public decimal LineAmount { get; init; }
    }
}
