using BusinessERP.Application.Abstractions;
using BusinessERP.Infrastructure.Persistence;
using BusinessERP.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace BusinessERP.Api.Endpoints;

/// <summary>
/// Tenant-scoped customer master endpoints. Every operation runs inside the transaction that
/// establishes PostgreSQL RLS context; tenant/company identifiers are never accepted as authority
/// from the request body.
/// </summary>
public static class CustomerEndpoints
{
    public static IEndpointRouteBuilder MapCustomerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1/companies/{companyId:guid}/customers")
            .WithTags("Sales - Customers");
        api.MapGet("", ListCustomers).WithName("ListCustomers");
        api.MapPost("", CreateCustomer).WithName("CreateCustomer");
        return endpoints;
    }

    private static async Task<IResult> ListCustomers(
        Guid companyId,
        ITenantContext tenant,
        TenantTransactionRunner runner,
        CancellationToken ct) =>
        await runner.ExecuteAsync<IResult>(async (db, token) =>
        {
            if (!await db.Companies.AsNoTracking().AnyAsync(
                    x => x.Id == companyId && x.TenantId == tenant.TenantId, token))
                return Results.NotFound(new { error = "company_not_found" });

            var rows = await db.Database.SqlQuery<CustomerListItem>($"""
                SELECT id AS "Id",
                       customer_code AS "Code",
                       name AS "Name",
                       email AS "Email",
                       phone AS "Phone",
                       tax_number AS "TaxNumber",
                       is_active AS "IsActive",
                       created_at AS "CreatedAt"
                  FROM customers
                 WHERE tenant_id = {tenant.TenantId}
                   AND company_id = {companyId}
                 ORDER BY name, customer_code
                 LIMIT 500
                """).ToListAsync(token);

            return Results.Ok(rows);
        }, ct);

    private static async Task<IResult> CreateCustomer(
        Guid companyId,
        CreateCustomerRequest request,
        ITenantContext tenant,
        TenantTransactionRunner runner,
        CancellationToken ct)
    {
        var code = request.Code?.Trim().ToUpperInvariant();
        var name = request.Name?.Trim();
        var email = request.Email?.Trim();
        var phone = request.Phone?.Trim();
        var taxNumber = request.TaxNumber?.Trim();

        if (string.IsNullOrWhiteSpace(code) || code.Length > 64 ||
            code.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(name) || name.Length > 200 ||
            (email?.Length > 320) || (phone?.Length > 32) || (taxNumber?.Length > 32))
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["customer"] = ["Code and name are required; field lengths must respect the API limits."]
            });

        if (email is not null && (email.Length == 0 || !email.Contains('@') || email.Any(char.IsControl)))
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["email"] = ["Provide a valid email address or omit it."]
            });

        if (!CanManageCustomers(tenant.RoleCode))
            return Results.Forbid();

        return await runner.ExecuteAsync<IResult>(async (db, token) =>
        {
            var companyExists = await db.Companies.AsNoTracking().AnyAsync(
                x => x.Id == companyId && x.TenantId == tenant.TenantId, token);
            if (!companyExists)
                return Results.NotFound(new { error = "company_not_found" });

            var id = await db.Database.SqlQuery<Guid>($"""
                INSERT INTO customers
                    (tenant_id, company_id, customer_code, name, email, phone, tax_number)
                VALUES
                    ({tenant.TenantId}, {companyId}, {code}, {name}, {email}, {phone}, {taxNumber})
                RETURNING id AS "Value"
                """).SingleAsync(token);

            var created = await db.Database.SqlQuery<CustomerListItem>($"""
                SELECT id AS "Id",
                       customer_code AS "Code",
                       name AS "Name",
                       email AS "Email",
                       phone AS "Phone",
                       tax_number AS "TaxNumber",
                       is_active AS "IsActive",
                       created_at AS "CreatedAt"
                  FROM customers
                 WHERE tenant_id = {tenant.TenantId}
                   AND company_id = {companyId}
                   AND id = {id}
                """).SingleAsync(token);

            return Results.Created($"/api/v1/companies/{companyId}/customers/{id}", created);
        }, ct);
    }

    private static bool CanManageCustomers(string role) =>
        role.Equals("OWNER", StringComparison.OrdinalIgnoreCase) ||
        role.Equals("ADMIN", StringComparison.OrdinalIgnoreCase) ||
        role.Equals("ACCOUNTANT", StringComparison.OrdinalIgnoreCase) ||
        role.Equals("SALES", StringComparison.OrdinalIgnoreCase);

    public sealed record CreateCustomerRequest(
        string? Code,
        string? Name,
        string? Email,
        string? Phone,
        string? TaxNumber);

    public sealed class CustomerListItem
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
