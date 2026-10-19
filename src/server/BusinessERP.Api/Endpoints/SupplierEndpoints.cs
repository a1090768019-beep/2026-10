using System.Text.Json;
using BusinessERP.Application.Abstractions;
using BusinessERP.Infrastructure.Persistence;
using BusinessERP.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace BusinessERP.Api.Endpoints;

/// <summary>Tenant- and company-scoped supplier master data.</summary>
public static class SupplierEndpoints
{
    public static IEndpointRouteBuilder MapSupplierEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1/companies/{companyId:guid}/suppliers")
            .WithTags("Purchases - Suppliers");
        api.MapGet("", ListSuppliers).WithName("ListSuppliers");
        api.MapPost("", CreateSupplier).WithName("CreateSupplier");
        return endpoints;
    }

    private static async Task<IResult> ListSuppliers(
        Guid companyId, ITenantContext tenant, TenantTransactionRunner runner, CancellationToken ct) =>
        await runner.ExecuteAsync<IResult>(async (db, token) =>
        {
            if (!await db.Companies.AsNoTracking().AnyAsync(
                    x => x.Id == companyId && x.TenantId == tenant.TenantId, token))
                return Results.NotFound(new { error = "company_not_found" });

            var rows = await db.Database.SqlQuery<SupplierDto>($"""
                SELECT id AS "Id", supplier_code AS "Code", name AS "Name",
                       email AS "Email", phone AS "Phone", tax_number AS "TaxNumber",
                       is_active AS "IsActive", created_at AS "CreatedAt"
                  FROM suppliers
                 WHERE tenant_id = {tenant.TenantId} AND company_id = {companyId}
                 ORDER BY name, supplier_code
                 LIMIT 500
                """).ToListAsync(token);
            return Results.Ok(rows);
        }, ct);

    private static async Task<IResult> CreateSupplier(
        Guid companyId, CreateSupplierRequest request, ITenantContext tenant,
        HttpContext http, TenantTransactionRunner runner, CancellationToken ct)
    {
        if (!CanManageSuppliers(tenant.RoleCode)) return Results.Forbid();

        var code = (request.Code ?? "").Trim().ToUpperInvariant();
        var name = (request.Name ?? "").Trim();
        var email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim();
        var phone = string.IsNullOrWhiteSpace(request.Phone) ? null : request.Phone.Trim();
        var taxNumber = string.IsNullOrWhiteSpace(request.TaxNumber) ? null : request.TaxNumber.Trim();
        var errors = new Dictionary<string, string[]>();
        if (code.Length is < 1 or > 64 || code.Any(char.IsControl) ||
            code.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.')))
            errors["code"] = ["رمز المورد مطلوب ويقبل الحروف اللاتينية والأرقام والشرطة والنقطة والشرطة السفلية."];
        if (name.Length is < 1 or > 200 || name.Any(char.IsControl))
            errors["name"] = ["اسم المورد مطلوب ولا يتجاوز 200 حرف."];
        if (email?.Length > 320 || (email is not null && (!email.Contains('@') || email.Any(char.IsControl))))
            errors["email"] = ["البريد الإلكتروني غير صالح أو يتجاوز الحد المدعوم."];
        if (phone?.Length > 32 || taxNumber?.Length > 32)
            errors["contact"] = ["رقم الهاتف أو الرقم الضريبي يتجاوز الحد المدعوم."];
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        return await runner.ExecuteAsync<IResult>(async (db, token) =>
        {
            if (!await db.Companies.AsNoTracking().AnyAsync(
                    x => x.Id == companyId && x.TenantId == tenant.TenantId, token))
                return Results.NotFound(new { error = "company_not_found" });

            var lockKey = $"supplier-code:{tenant.TenantId:D}:{companyId:D}:{code}";
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", token);
            var exists = await db.Database.SqlQuery<int>($"""
                SELECT count(*)::integer AS "Value" FROM suppliers
                 WHERE tenant_id = {tenant.TenantId} AND company_id = {companyId} AND supplier_code = {code}
                """).SingleAsync(token);
            if (exists != 0)
                return Results.Conflict(new { error = "supplier_code_conflict", message = "رمز المورد مستخدم بالفعل في هذه الشركة." });

            var id = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO suppliers (id, tenant_id, company_id, supplier_code, name, email, phone, tax_number)
                VALUES ({id}, {tenant.TenantId}, {companyId}, {code}, {name}, {email}, {phone}, {taxNumber})
                """, token);

            db.AuditLogs.Add(new AuditLogRecord
            {
                Id = Guid.NewGuid(), TenantId = tenant.TenantId, UserId = tenant.UserId,
                Action = "SUPPLIER_CREATED", EntityType = "Supplier", EntityId = id.ToString("D"),
                IpAddress = http.Connection.RemoteIpAddress?.ToString(),
                DetailsJson = JsonSerializer.Serialize(new { Code = code, Name = name }),
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync(token);

            var created = await db.Database.SqlQuery<SupplierDto>($"""
                SELECT id AS "Id", supplier_code AS "Code", name AS "Name",
                       email AS "Email", phone AS "Phone", tax_number AS "TaxNumber",
                       is_active AS "IsActive", created_at AS "CreatedAt"
                  FROM suppliers
                 WHERE tenant_id = {tenant.TenantId} AND company_id = {companyId} AND id = {id}
                """).SingleAsync(token);
            return Results.Created($"/api/v1/companies/{companyId}/suppliers/{id}", created);
        }, ct);
    }

    private static bool CanManageSuppliers(string role) =>
        role.Equals("OWNER", StringComparison.OrdinalIgnoreCase) ||
        role.Equals("ADMIN", StringComparison.OrdinalIgnoreCase) ||
        role.Equals("ACCOUNTANT", StringComparison.OrdinalIgnoreCase);

    public sealed record CreateSupplierRequest(string? Code, string? Name, string? Email, string? Phone, string? TaxNumber);
    public sealed class SupplierDto
    {
        public Guid Id { get; init; }
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public string? Email { get; init; }
        public string? Phone { get; init; }
        public string? TaxNumber { get; init; }
        public bool IsActive { get; init; }
        public DateTime CreatedAt { get; init; }
    }
}
