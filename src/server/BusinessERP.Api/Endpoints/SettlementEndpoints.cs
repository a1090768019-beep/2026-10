using System.Text.Json;
using BusinessERP.Application.Abstractions;
using BusinessERP.Infrastructure.Persistence;
using BusinessERP.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace BusinessERP.Api.Endpoints;

/// <summary>
/// Posts customer receipts and supplier payments against already-posted invoices.
/// This is internal cash-settlement bookkeeping; it is not a bank-feed or payment-provider integration.
/// </summary>
public static class SettlementEndpoints
{
    private static readonly string[] AllowedRoles = ["OWNER", "ADMIN", "ACCOUNTANT"];

    public static IEndpointRouteBuilder MapSettlementEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/companies/{companyId:guid}/customer-receipts", ListCustomerReceipts)
            .WithName("ListCustomerReceipts").WithTags("Settlements");
        endpoints.MapPost("/api/v1/companies/{companyId:guid}/customer-receipts", CreateCustomerReceipt)
            .WithName("CreateCustomerReceipt").WithTags("Settlements");
        endpoints.MapPost("/api/v1/companies/{companyId:guid}/customer-receipts/{receiptId:guid}/post-to-ledger", PostCustomerReceipt)
            .WithName("PostCustomerReceipt").WithTags("Settlements");

        endpoints.MapGet("/api/v1/companies/{companyId:guid}/supplier-payments", ListSupplierPayments)
            .WithName("ListSupplierPayments").WithTags("Settlements");
        endpoints.MapPost("/api/v1/companies/{companyId:guid}/supplier-payments", CreateSupplierPayment)
            .WithName("CreateSupplierPayment").WithTags("Settlements");
        endpoints.MapPost("/api/v1/companies/{companyId:guid}/supplier-payments/{paymentId:guid}/post-to-ledger", PostSupplierPayment)
            .WithName("PostSupplierPayment").WithTags("Settlements");
        return endpoints;
    }

    private static async Task<IResult> ListCustomerReceipts(
        Guid companyId, ITenantContext tenant, TenantTransactionRunner runner, CancellationToken ct)
    {
        if (!AllowedRoles.Contains(tenant.RoleCode, StringComparer.OrdinalIgnoreCase)) return Results.Forbid();
        return await runner.ExecuteAsync<IResult>(async (db, token) =>
        {
            var rows = await db.Database.SqlQuery<CustomerReceiptListRow>($"""
                SELECT r.id AS "Id", r.receipt_number AS "Number", r.receipt_date AS "Date",
                       r.customer_id AS "PartyId", c.name AS "PartyName", r.currency_code AS "CurrencyCode",
                       r.amount AS "Amount", r.status AS "Status", r.accounting_posted_journal_entry_id AS "JournalEntryId"
                  FROM customer_receipts r
                  JOIN customers c ON c.tenant_id=r.tenant_id AND c.company_id=r.company_id AND c.id=r.customer_id
                 WHERE r.tenant_id={tenant.TenantId} AND r.company_id={companyId}
                 ORDER BY r.receipt_date DESC, r.created_at DESC
                 LIMIT 500
                """).ToListAsync(token);
            return Results.Ok(rows);
        }, ct);
    }

    private static async Task<IResult> CreateCustomerReceipt(
        Guid companyId, CreateCustomerReceiptRequest request, ITenantContext tenant,
        HttpContext http, TenantTransactionRunner runner, CancellationToken ct)
    {
        if (!AllowedRoles.Contains(tenant.RoleCode, StringComparer.OrdinalIgnoreCase)) return Results.Forbid();
        var errors = ValidateCreateRequest(request.PartyId, request.Number, request.Amount, request.Notes, request.Allocations);
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        return await runner.ExecuteAsync<IResult>(async (db, token) =>
        {
            var company = await db.Companies.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == companyId && x.TenantId == tenant.TenantId, token);
            if (company is null) return Results.NotFound(new { error = "company_not_found" });
            if (!string.Equals(company.BaseCurrency.Trim(), (request.CurrencyCode ?? company.BaseCurrency).Trim(), StringComparison.OrdinalIgnoreCase))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["currencyCode"] = ["عملة السند يجب أن تطابق العملة الأساسية للشركة في هذه النسخة."] });

            var number = NormalizeNumber(request.Number);
            await LockAsync(db, $"customer-receipt-number:{tenant.TenantId:D}:{companyId:D}:{number}", token);
            var party = await db.Database.SqlQuery<PartyRow>($"""
                SELECT id AS "Id", is_active AS "IsActive"
                  FROM customers
                 WHERE tenant_id={tenant.TenantId} AND company_id={companyId} AND id={request.PartyId}
                 LIMIT 1
                """).SingleOrDefaultAsync(token);
            if (party is null || !party.IsActive)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["customerId"] = ["العميل غير موجود أو غير نشط في الشركة الحالية."] });

            var allocations = NormalizeAllocations(request.Allocations);
            var totalAllocated = allocations.Sum(x => x.Amount);
            if (totalAllocated != request.Amount)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["allocations"] = ["يجب أن يساوي مجموع التخصيصات مبلغ سند القبض بالكامل."] });

            foreach (var allocation in allocations)
            {
                var invoice = await GetSalesInvoiceBalanceAsync(db, tenant.TenantId, companyId, allocation.InvoiceId, token);
                if (invoice is null || invoice.Status != "ACCOUNTING_POSTED" || invoice.PartyId != request.PartyId)
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["allocations"] = ["كل تخصيص يجب أن يشير إلى فاتورة مبيعات مرحلة للعميل نفسه."] });
                if (!string.Equals(invoice.CurrencyCode.Trim(), (request.CurrencyCode ?? company.BaseCurrency).Trim(), StringComparison.OrdinalIgnoreCase))
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["allocations"] = ["عملة سند القبض يجب أن تطابق عملة الفاتورة."] });
                if (allocation.Amount > invoice.Outstanding)
                    return Results.Conflict(new { error = "receipt_allocation_exceeds_invoice_balance", invoiceId = allocation.InvoiceId, outstanding = invoice.Outstanding });
            }

            var id = Guid.NewGuid();
            var currency = (request.CurrencyCode ?? company.BaseCurrency).Trim().ToUpperInvariant();
            var notes = NormalizeNotes(request.Notes);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO customer_receipts
                    (id,tenant_id,company_id,customer_id,receipt_number,receipt_date,currency_code,amount,notes,status,created_by)
                VALUES ({id},{tenant.TenantId},{companyId},{request.PartyId},{number},{request.Date},{currency},{request.Amount},{notes},'DRAFT',{tenant.UserId})
                """, token);
            foreach (var allocation in allocations)
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO customer_receipt_allocations
                        (id,tenant_id,company_id,receipt_id,sales_invoice_draft_id,allocated_amount)
                    VALUES ({Guid.NewGuid()},{tenant.TenantId},{companyId},{id},{allocation.InvoiceId},{allocation.Amount})
                    """, token);

            db.AuditLogs.Add(new AuditLogRecord
            {
                Id = Guid.NewGuid(), TenantId = tenant.TenantId, UserId = tenant.UserId,
                Action = "CUSTOMER_RECEIPT_CREATED", EntityType = "CustomerReceipt", EntityId = id.ToString("D"),
                IpAddress = http.Connection.RemoteIpAddress?.ToString(),
                DetailsJson = JsonSerializer.Serialize(new { ReceiptNumber = number, request.Amount, AllocationCount = allocations.Count }),
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync(token);
            return Results.Created($"/api/v1/companies/{companyId:D}/customer-receipts/{id:D}",
                new { Id = id, Number = number, request.Date, PartyId = request.PartyId, CurrencyCode = currency, request.Amount, Status = "DRAFT" });
        }, ct);
    }

    private static async Task<IResult> PostCustomerReceipt(
        Guid companyId, Guid receiptId, PostCustomerReceiptRequest request, ITenantContext tenant,
        HttpContext http, TenantTransactionRunner runner, CancellationToken ct)
    {
        if (!AllowedRoles.Contains(tenant.RoleCode, StringComparer.OrdinalIgnoreCase)) return Results.Forbid();
        var entryNumber = NormalizeEntryNumber(request.EntryNumber);
        if (entryNumber is null || request.FiscalPeriodId == Guid.Empty ||
            request.CashAccountId == Guid.Empty || request.ReceivableAccountId == Guid.Empty)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["posting"] = ["رقم القيد والفترة وحساب النقدية وحساب العملاء مطلوبة."] });

        return await runner.ExecuteAsync<IResult>(async (db, token) =>
        {
            var receiptLock = $"customer-receipt-posting:{tenant.TenantId:D}:{companyId:D}:{receiptId:D}";
            await LockAsync(db, receiptLock, token);
            var receipt = await db.Database.SqlQuery<SettlementSource>($"""
                SELECT id AS "Id", customer_id AS "PartyId", receipt_number AS "Number",
                       receipt_date AS "Date", currency_code AS "CurrencyCode", amount AS "Amount",
                       status AS "Status", accounting_posted_journal_entry_id AS "JournalEntryId"
                  FROM customer_receipts
                 WHERE tenant_id={tenant.TenantId} AND company_id={companyId} AND id={receiptId}
                 LIMIT 1
                """).SingleOrDefaultAsync(token);
            if (receipt is null) return Results.NotFound(new { error = "customer_receipt_not_found" });
            if (receipt.Status == "ACCOUNTING_POSTED")
                return await ExistingPostingAsync(db, tenant.TenantId, companyId, receipt, "CUSTOMER_RECEIPT", token);
            if (receipt.Status != "DRAFT") return Results.Conflict(new { error = "customer_receipt_not_postable" });

            var company = await db.Companies.AsNoTracking().SingleOrDefaultAsync(
                x => x.Id == companyId && x.TenantId == tenant.TenantId, token);
            if (company is null) return Results.NotFound(new { error = "company_not_found" });
            if (!string.Equals(company.BaseCurrency.Trim(), receipt.CurrencyCode.Trim(), StringComparison.OrdinalIgnoreCase))
                return Results.Conflict(new { error = "settlement_currency_mismatch" });

            var allocations = await db.Database.SqlQuery<SettlementAllocationRow>($"""
                SELECT sales_invoice_draft_id AS "InvoiceId", allocated_amount AS "Amount"
                  FROM customer_receipt_allocations
                 WHERE tenant_id={tenant.TenantId} AND company_id={companyId} AND receipt_id={receiptId}
                 ORDER BY sales_invoice_draft_id
                """).ToListAsync(token);
            if (allocations.Count == 0 || allocations.Sum(x => x.Amount) != receipt.Amount)
                return Results.Conflict(new { error = "receipt_allocations_must_equal_amount" });

            foreach (var allocation in allocations.OrderBy(x => x.InvoiceId))
                await LockAsync(db, $"customer-invoice-balance:{tenant.TenantId:D}:{companyId:D}:{allocation.InvoiceId:D}", token);
            foreach (var allocation in allocations)
            {
                var invoice = await GetSalesInvoiceBalanceAsync(db, tenant.TenantId, companyId, allocation.InvoiceId, token);
                if (invoice is null || invoice.Status != "ACCOUNTING_POSTED" || invoice.PartyId != receipt.PartyId ||
                    !string.Equals(invoice.CurrencyCode.Trim(), receipt.CurrencyCode.Trim(), StringComparison.OrdinalIgnoreCase))
                    return Results.Conflict(new { error = "receipt_invoice_link_invalid", invoiceId = allocation.InvoiceId });
                if (allocation.Amount > invoice.Outstanding)
                    return Results.Conflict(new { error = "receipt_allocation_exceeds_invoice_balance", invoiceId = allocation.InvoiceId, outstanding = invoice.Outstanding });
            }

            var period = await db.FiscalPeriods.AsNoTracking().SingleOrDefaultAsync(
                x => x.Id == request.FiscalPeriodId && x.CompanyId == companyId && x.TenantId == tenant.TenantId, token);
            if (period is null) return Results.ValidationProblem(new Dictionary<string, string[]> { ["fiscalPeriodId"] = ["الفترة المالية غير موجودة في الشركة الحالية."] });
            if (period.Status != "OPEN" || receipt.Date < period.StartDate || receipt.Date > period.EndDate)
                return Results.Conflict(new { error = "fiscal_period_not_open_for_receipt_date" });

            var accounts = await LoadAccountsAsync(db, tenant.TenantId, companyId, [request.CashAccountId, request.ReceivableAccountId], token);
            if (accounts.Count != 2) return Results.ValidationProblem(new Dictionary<string, string[]> { ["accounts"] = ["حساب النقدية أو العملاء غير موجود في الشركة الحالية."] });
            var cash = accounts.Single(x => x.Id == request.CashAccountId);
            var receivable = accounts.Single(x => x.Id == request.ReceivableAccountId);
            if (!IsPostable(cash, "ASSET", "DEBIT") || !IsPostable(receivable, "ASSET", "DEBIT") || cash.Id == receivable.Id)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["accounts"] = ["اختر حساب نقدية/بنك أصل مدين وحساب عملاء أصل مدين مختلفًا عنه."] });

            var entry = await CreateAndPostJournalAsync(db, tenant.TenantId, companyId, period.Id, entryNumber,
                receipt.Date, receipt.CurrencyCode, "CUSTOMER_RECEIPT", receipt.Id, $"Customer receipt {receipt.Number}",
                [(cash.Id, "Customer receipt cash/bank", receipt.Amount, 0m),
                 (receivable.Id, "Settlement of customer receivable", 0m, receipt.Amount)], token);
            var postedAt = entry.PostedAt!.Value;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE customer_receipts SET status='ACCOUNTING_POSTED',
                    accounting_posted_journal_entry_id={entry.Id}, accounting_posted_at={postedAt}
                 WHERE tenant_id={tenant.TenantId} AND company_id={companyId} AND id={receiptId}
                """, token);
            await AddAuditAsync(db, tenant, http, "CUSTOMER_RECEIPT_POSTED", "CustomerReceipt", receipt.Id,
                new { JournalEntryId = entry.Id, EntryNumber = entry.EntryNumber, receipt.Amount, AllocationCount = allocations.Count }, token);
            return Results.Ok(new SettlementPostingResult(receipt.Id, entry.Id, entry.EntryNumber, entry.Status, entry.EntryDate, receipt.Amount, false));
        }, ct);
    }

    private static async Task<IResult> ListSupplierPayments(
        Guid companyId, ITenantContext tenant, TenantTransactionRunner runner, CancellationToken ct)
    {
        if (!AllowedRoles.Contains(tenant.RoleCode, StringComparer.OrdinalIgnoreCase)) return Results.Forbid();
        return await runner.ExecuteAsync<IResult>(async (db, token) =>
        {
            var rows = await db.Database.SqlQuery<SupplierPaymentListRow>($"""
                SELECT p.id AS "Id", p.payment_number AS "Number", p.payment_date AS "Date",
                       p.supplier_id AS "PartyId", s.name AS "PartyName", p.currency_code AS "CurrencyCode",
                       p.amount AS "Amount", p.status AS "Status", p.accounting_posted_journal_entry_id AS "JournalEntryId"
                  FROM supplier_payments p
                  JOIN suppliers s ON s.tenant_id=p.tenant_id AND s.company_id=p.company_id AND s.id=p.supplier_id
                 WHERE p.tenant_id={tenant.TenantId} AND p.company_id={companyId}
                 ORDER BY p.payment_date DESC, p.created_at DESC
                 LIMIT 500
                """).ToListAsync(token);
            return Results.Ok(rows);
        }, ct);
    }

    private static async Task<IResult> CreateSupplierPayment(
        Guid companyId, CreateSupplierPaymentRequest request, ITenantContext tenant,
        HttpContext http, TenantTransactionRunner runner, CancellationToken ct)
    {
        if (!AllowedRoles.Contains(tenant.RoleCode, StringComparer.OrdinalIgnoreCase)) return Results.Forbid();
        var errors = ValidateCreateRequest(request.PartyId, request.Number, request.Amount, request.Notes, request.Allocations);
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        return await runner.ExecuteAsync<IResult>(async (db, token) =>
        {
            var company = await db.Companies.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == companyId && x.TenantId == tenant.TenantId, token);
            if (company is null) return Results.NotFound(new { error = "company_not_found" });
            var currencyCode = (request.CurrencyCode ?? company.BaseCurrency).Trim();
            if (!string.Equals(company.BaseCurrency.Trim(), currencyCode, StringComparison.OrdinalIgnoreCase))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["currencyCode"] = ["عملة السند يجب أن تطابق العملة الأساسية للشركة في هذه النسخة."] });

            var number = NormalizeNumber(request.Number);
            await LockAsync(db, $"supplier-payment-number:{tenant.TenantId:D}:{companyId:D}:{number}", token);
            var party = await db.Database.SqlQuery<PartyRow>($"""
                SELECT id AS "Id", is_active AS "IsActive"
                  FROM suppliers
                 WHERE tenant_id={tenant.TenantId} AND company_id={companyId} AND id={request.PartyId}
                 LIMIT 1
                """).SingleOrDefaultAsync(token);
            if (party is null || !party.IsActive)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["supplierId"] = ["المورد غير موجود أو غير نشط في الشركة الحالية."] });

            var allocations = NormalizeAllocations(request.Allocations);
            if (allocations.Sum(x => x.Amount) != request.Amount)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["allocations"] = ["يجب أن يساوي مجموع التخصيصات مبلغ سند الصرف بالكامل."] });
            foreach (var allocation in allocations)
            {
                var invoice = await GetPurchaseInvoiceBalanceAsync(db, tenant.TenantId, companyId, allocation.InvoiceId, token);
                if (invoice is null || invoice.Status != "ACCOUNTING_POSTED" || invoice.PartyId != request.PartyId)
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["allocations"] = ["كل تخصيص يجب أن يشير إلى فاتورة شراء مرحلة للمورد نفسه."] });
                if (!string.Equals(invoice.CurrencyCode.Trim(), currencyCode, StringComparison.OrdinalIgnoreCase))
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["allocations"] = ["عملة سند الصرف يجب أن تطابق عملة الفاتورة."] });
                if (allocation.Amount > invoice.Outstanding)
                    return Results.Conflict(new { error = "payment_allocation_exceeds_invoice_balance", invoiceId = allocation.InvoiceId, outstanding = invoice.Outstanding });
            }

            var id = Guid.NewGuid();
            var notes = NormalizeNotes(request.Notes);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO supplier_payments
                    (id,tenant_id,company_id,supplier_id,payment_number,payment_date,currency_code,amount,notes,status,created_by)
                VALUES ({id},{tenant.TenantId},{companyId},{request.PartyId},{number},{request.Date},{currencyCode.ToUpperInvariant()},{request.Amount},{notes},'DRAFT',{tenant.UserId})
                """, token);
            foreach (var allocation in allocations)
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO supplier_payment_allocations
                        (id,tenant_id,company_id,payment_id,purchase_invoice_draft_id,allocated_amount)
                    VALUES ({Guid.NewGuid()},{tenant.TenantId},{companyId},{id},{allocation.InvoiceId},{allocation.Amount})
                    """, token);
            db.AuditLogs.Add(new AuditLogRecord
            {
                Id = Guid.NewGuid(), TenantId = tenant.TenantId, UserId = tenant.UserId,
                Action = "SUPPLIER_PAYMENT_CREATED", EntityType = "SupplierPayment", EntityId = id.ToString("D"),
                IpAddress = http.Connection.RemoteIpAddress?.ToString(),
                DetailsJson = JsonSerializer.Serialize(new { PaymentNumber = number, request.Amount, AllocationCount = allocations.Count }),
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync(token);
            return Results.Created($"/api/v1/companies/{companyId:D}/supplier-payments/{id:D}",
                new { Id = id, Number = number, request.Date, PartyId = request.PartyId, CurrencyCode = currencyCode.ToUpperInvariant(), request.Amount, Status = "DRAFT" });
        }, ct);
    }

    private static async Task<IResult> PostSupplierPayment(
        Guid companyId, Guid paymentId, PostSupplierPaymentRequest request, ITenantContext tenant,
        HttpContext http, TenantTransactionRunner runner, CancellationToken ct)
    {
        if (!AllowedRoles.Contains(tenant.RoleCode, StringComparer.OrdinalIgnoreCase)) return Results.Forbid();
        var entryNumber = NormalizeEntryNumber(request.EntryNumber);
        if (entryNumber is null || request.FiscalPeriodId == Guid.Empty ||
            request.CashAccountId == Guid.Empty || request.PayableAccountId == Guid.Empty)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["posting"] = ["رقم القيد والفترة وحساب النقدية وحساب الموردين مطلوبة."] });

        return await runner.ExecuteAsync<IResult>(async (db, token) =>
        {
            await LockAsync(db, $"supplier-payment-posting:{tenant.TenantId:D}:{companyId:D}:{paymentId:D}", token);
            var payment = await db.Database.SqlQuery<SettlementSource>($"""
                SELECT id AS "Id", supplier_id AS "PartyId", payment_number AS "Number",
                       payment_date AS "Date", currency_code AS "CurrencyCode", amount AS "Amount",
                       status AS "Status", accounting_posted_journal_entry_id AS "JournalEntryId"
                  FROM supplier_payments
                 WHERE tenant_id={tenant.TenantId} AND company_id={companyId} AND id={paymentId}
                 LIMIT 1
                """).SingleOrDefaultAsync(token);
            if (payment is null) return Results.NotFound(new { error = "supplier_payment_not_found" });
            if (payment.Status == "ACCOUNTING_POSTED")
                return await ExistingPostingAsync(db, tenant.TenantId, companyId, payment, "SUPPLIER_PAYMENT", token);
            if (payment.Status != "DRAFT") return Results.Conflict(new { error = "supplier_payment_not_postable" });

            var company = await db.Companies.AsNoTracking().SingleOrDefaultAsync(
                x => x.Id == companyId && x.TenantId == tenant.TenantId, token);
            if (company is null) return Results.NotFound(new { error = "company_not_found" });
            if (!string.Equals(company.BaseCurrency.Trim(), payment.CurrencyCode.Trim(), StringComparison.OrdinalIgnoreCase))
                return Results.Conflict(new { error = "settlement_currency_mismatch" });

            var allocations = await db.Database.SqlQuery<SettlementAllocationRow>($"""
                SELECT purchase_invoice_draft_id AS "InvoiceId", allocated_amount AS "Amount"
                  FROM supplier_payment_allocations
                 WHERE tenant_id={tenant.TenantId} AND company_id={companyId} AND payment_id={paymentId}
                 ORDER BY purchase_invoice_draft_id
                """).ToListAsync(token);
            if (allocations.Count == 0 || allocations.Sum(x => x.Amount) != payment.Amount)
                return Results.Conflict(new { error = "payment_allocations_must_equal_amount" });
            foreach (var allocation in allocations.OrderBy(x => x.InvoiceId))
                await LockAsync(db, $"supplier-invoice-balance:{tenant.TenantId:D}:{companyId:D}:{allocation.InvoiceId:D}", token);
            foreach (var allocation in allocations)
            {
                var invoice = await GetPurchaseInvoiceBalanceAsync(db, tenant.TenantId, companyId, allocation.InvoiceId, token);
                if (invoice is null || invoice.Status != "ACCOUNTING_POSTED" || invoice.PartyId != payment.PartyId ||
                    !string.Equals(invoice.CurrencyCode.Trim(), payment.CurrencyCode.Trim(), StringComparison.OrdinalIgnoreCase))
                    return Results.Conflict(new { error = "payment_invoice_link_invalid", invoiceId = allocation.InvoiceId });
                if (allocation.Amount > invoice.Outstanding)
                    return Results.Conflict(new { error = "payment_allocation_exceeds_invoice_balance", invoiceId = allocation.InvoiceId, outstanding = invoice.Outstanding });
            }

            var period = await db.FiscalPeriods.AsNoTracking().SingleOrDefaultAsync(
                x => x.Id == request.FiscalPeriodId && x.CompanyId == companyId && x.TenantId == tenant.TenantId, token);
            if (period is null) return Results.ValidationProblem(new Dictionary<string, string[]> { ["fiscalPeriodId"] = ["الفترة المالية غير موجودة في الشركة الحالية."] });
            if (period.Status != "OPEN" || payment.Date < period.StartDate || payment.Date > period.EndDate)
                return Results.Conflict(new { error = "fiscal_period_not_open_for_payment_date" });

            var accounts = await LoadAccountsAsync(db, tenant.TenantId, companyId, [request.CashAccountId, request.PayableAccountId], token);
            if (accounts.Count != 2) return Results.ValidationProblem(new Dictionary<string, string[]> { ["accounts"] = ["حساب النقدية أو الموردين غير موجود في الشركة الحالية."] });
            var cash = accounts.Single(x => x.Id == request.CashAccountId);
            var payable = accounts.Single(x => x.Id == request.PayableAccountId);
            if (!IsPostable(cash, "ASSET", "DEBIT") || !IsPostable(payable, "LIABILITY", "CREDIT") || cash.Id == payable.Id)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["accounts"] = ["اختر حساب نقدية/بنك أصل مدين وحساب موردين التزام دائن مختلفًا عنه."] });

            var entry = await CreateAndPostJournalAsync(db, tenant.TenantId, companyId, period.Id, entryNumber,
                payment.Date, payment.CurrencyCode, "SUPPLIER_PAYMENT", payment.Id, $"Supplier payment {payment.Number}",
                [(payable.Id, "Settlement of supplier payable", payment.Amount, 0m),
                 (cash.Id, "Supplier payment cash/bank", 0m, payment.Amount)], token);
            var postedAt = entry.PostedAt!.Value;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE supplier_payments SET status='ACCOUNTING_POSTED',
                    accounting_posted_journal_entry_id={entry.Id}, accounting_posted_at={postedAt}
                 WHERE tenant_id={tenant.TenantId} AND company_id={companyId} AND id={paymentId}
                """, token);
            await AddAuditAsync(db, tenant, http, "SUPPLIER_PAYMENT_POSTED", "SupplierPayment", payment.Id,
                new { JournalEntryId = entry.Id, EntryNumber = entry.EntryNumber, payment.Amount, AllocationCount = allocations.Count }, token);
            return Results.Ok(new SettlementPostingResult(payment.Id, entry.Id, entry.EntryNumber, entry.Status, entry.EntryDate, payment.Amount, false));
        }, ct);
    }

    private static Dictionary<string, string[]> ValidateCreateRequest(
        Guid partyId, string? number, decimal amount, string? notes, IReadOnlyList<SettlementAllocationRequest>? allocations)
    {
        var errors = new Dictionary<string, string[]>();
        if (partyId == Guid.Empty) errors["partyId"] = ["العميل أو المورد مطلوب."];
        if (NormalizeEntryNumber(number) is null) errors["number"] = ["رقم السند مطلوب وبصيغة أحرف لاتينية وأرقام وشرطة وشرطة سفلية وشرطة مائلة ونقطة."];
        if (amount <= 0m || decimal.Round(amount, 2, MidpointRounding.ToEven) != amount)
            errors["amount"] = ["المبلغ يجب أن يكون موجبًا وبحد أقصى منزلتين عشريتين."];
        if (notes is { Length: > 2000 } || (notes ?? "").Any(char.IsControl) && (notes ?? "").Any(ch => ch is not '\r' and not '\n' and not '\t'))
            errors["notes"] = ["الملاحظات لا تتجاوز 2000 حرف ولا تقبل محارف التحكم."];
        if (allocations is null || allocations.Count == 0 || allocations.Count > 100)
            errors["allocations"] = ["يجب تخصيص السند لفاتورة واحدة على الأقل وبحد أقصى 100 فاتورة."];
        else
        {
            if (allocations.Any(x => x.InvoiceId == Guid.Empty || x.Amount <= 0m || decimal.Round(x.Amount, 2, MidpointRounding.ToEven) != x.Amount))
                errors["allocations"] = ["كل تخصيص يحتاج فاتورة صحيحة ومبلغًا موجبًا بمنزلتين عشريتين."];
            if (allocations.Select(x => x.InvoiceId).Distinct().Count() != allocations.Count)
                errors["allocations"] = ["لا تكرر الفاتورة في تخصيصات السند."];
        }
        return errors;
    }

    private static List<NormalizedAllocation> NormalizeAllocations(IReadOnlyList<SettlementAllocationRequest>? input) =>
        (input ?? []).Select(x => new NormalizedAllocation(x.InvoiceId, x.Amount)).OrderBy(x => x.InvoiceId).ToList();

    private static string NormalizeNumber(string? input) => (input ?? "").Trim().ToUpperInvariant();
    private static string NormalizeNotes(string? input) => (input ?? "").Trim();
    private static string? NormalizeEntryNumber(string? input)
    {
        var value = (input ?? "").Trim().ToUpperInvariant();
        return value.Length is < 1 or > 80 ||
               value.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '/' or '.'))
            ? null : value;
    }

    private static async Task LockAsync(ErpDbContext db, string key, CancellationToken ct) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);

    private static async Task<SalesInvoiceBalance?> GetSalesInvoiceBalanceAsync(
        ErpDbContext db, Guid tenantId, Guid companyId, Guid invoiceId, CancellationToken ct) =>
        await db.Database.SqlQuery<SalesInvoiceBalance>($"""
            SELECT i.id AS "InvoiceId", i.customer_id AS "PartyId", i.status AS "Status",
                   i.currency_code AS "CurrencyCode", i.total_amount AS "TotalAmount",
                   i.total_amount - COALESCE((
                       SELECT sum(a.allocated_amount)
                         FROM customer_receipt_allocations a
                         JOIN customer_receipts r ON r.tenant_id=a.tenant_id AND r.company_id=a.company_id AND r.id=a.receipt_id
                        WHERE a.tenant_id=i.tenant_id AND a.company_id=i.company_id
                          AND a.sales_invoice_draft_id=i.id AND r.status='ACCOUNTING_POSTED'
                   ),0) AS "Outstanding"
              FROM sales_invoice_drafts i
             WHERE i.tenant_id={tenantId} AND i.company_id={companyId} AND i.id={invoiceId}
             LIMIT 1
            """).SingleOrDefaultAsync(ct);

    private static async Task<SupplierInvoiceBalance?> GetPurchaseInvoiceBalanceAsync(
        ErpDbContext db, Guid tenantId, Guid companyId, Guid invoiceId, CancellationToken ct) =>
        await db.Database.SqlQuery<SupplierInvoiceBalance>($"""
            SELECT i.id AS "InvoiceId", i.supplier_id AS "PartyId", i.status AS "Status",
                   i.currency_code AS "CurrencyCode", i.total_amount AS "TotalAmount",
                   i.total_amount - COALESCE((
                       SELECT sum(a.allocated_amount)
                         FROM supplier_payment_allocations a
                         JOIN supplier_payments p ON p.tenant_id=a.tenant_id AND p.company_id=a.company_id AND p.id=a.payment_id
                        WHERE a.tenant_id=i.tenant_id AND a.company_id=i.company_id
                          AND a.purchase_invoice_draft_id=i.id AND p.status='ACCOUNTING_POSTED'
                   ),0) AS "Outstanding"
              FROM purchase_invoice_drafts i
             WHERE i.tenant_id={tenantId} AND i.company_id={companyId} AND i.id={invoiceId}
             LIMIT 1
            """).SingleOrDefaultAsync(ct);

    private static async Task<List<AccountRecord>> LoadAccountsAsync(
        ErpDbContext db, Guid tenantId, Guid companyId, Guid[] ids, CancellationToken ct) =>
        await db.Accounts.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.CompanyId == companyId && ids.Contains(x.Id))
            .ToListAsync(ct);

    private static bool IsPostable(AccountRecord account, string type, string normalBalance) =>
        account.AccountType == type && account.NormalBalance == normalBalance && account.IsActive && account.AllowPosting;

    private static async Task<JournalEntryRecord> CreateAndPostJournalAsync(
        ErpDbContext db, Guid tenantId, Guid companyId, Guid fiscalPeriodId, string entryNumber,
        DateOnly entryDate, string currencyCode, string sourceType, Guid sourceId, string description,
        (Guid AccountId, string Description, decimal Debit, decimal Credit)[] lines, CancellationToken ct)
    {
        var numberLock = $"journal-entry-number:{tenantId:D}:{companyId:D}:{entryNumber}";
        await LockAsync(db, numberLock, ct);
        if (await db.JournalEntries.AnyAsync(x => x.TenantId == tenantId && x.CompanyId == companyId && x.EntryNumber == entryNumber, ct))
            throw new SettlementConflictException("journal_entry_number_conflict");

        if (await db.JournalEntries.AnyAsync(x => x.TenantId == tenantId && x.CompanyId == companyId &&
                x.SourceType == sourceType && x.SourceId == sourceId, ct))
            throw new SettlementConflictException("settlement_already_has_journal");

        var now = DateTime.UtcNow;
        var entry = new JournalEntryRecord
        {
            Id = Guid.NewGuid(), TenantId = tenantId, CompanyId = companyId, FiscalPeriodId = fiscalPeriodId,
            EntryNumber = entryNumber, EntryDate = entryDate, Description = description,
            CurrencyCode = currencyCode.Trim().ToUpperInvariant(), Status = "DRAFT",
            SourceType = sourceType, SourceId = sourceId, CreatedAt = now
        };
        db.JournalEntries.Add(entry);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            db.JournalEntryLines.Add(new JournalEntryLineRecord
            {
                Id = Guid.NewGuid(), TenantId = tenantId, CompanyId = companyId, JournalEntryId = entry.Id,
                AccountId = line.AccountId, LineNumber = i + 1, Description = line.Description,
                Debit = line.Debit, Credit = line.Credit
            });
        }
        await db.SaveChangesAsync(ct);
        entry.Status = "APPROVED";
        await db.SaveChangesAsync(ct);
        entry.Status = "POSTED";
        entry.PostedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return entry;
    }

    private static async Task<IResult> ExistingPostingAsync(
        ErpDbContext db, Guid tenantId, Guid companyId, SettlementSource source, string sourceType, CancellationToken ct)
    {
        if (!source.JournalEntryId.HasValue)
            return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Settlement journal link is incomplete");
        var entry = await db.JournalEntries.AsNoTracking().SingleOrDefaultAsync(x =>
            x.TenantId == tenantId && x.CompanyId == companyId && x.Id == source.JournalEntryId.Value &&
            x.SourceType == sourceType && x.SourceId == source.Id && x.Status == "POSTED", ct);
        if (entry is null)
            return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Posted settlement journal was not found");
        return Results.Ok(new SettlementPostingResult(source.Id, entry.Id, entry.EntryNumber, entry.Status, entry.EntryDate, source.Amount, true));
    }

    private static async Task AddAuditAsync(
        ErpDbContext db, ITenantContext tenant, HttpContext http, string action, string entityType, Guid entityId,
        object details, CancellationToken ct)
    {
        db.AuditLogs.Add(new AuditLogRecord
        {
            Id = Guid.NewGuid(), TenantId = tenant.TenantId, UserId = tenant.UserId,
            Action = action, EntityType = entityType, EntityId = entityId.ToString("D"),
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
            DetailsJson = JsonSerializer.Serialize(details), CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct);
    }

    public sealed record SettlementAllocationRequest(Guid InvoiceId, decimal Amount);
    public sealed record CreateCustomerReceiptRequest(Guid PartyId, string? Number, DateOnly Date, decimal Amount,
        string? CurrencyCode, string? Notes, IReadOnlyList<SettlementAllocationRequest>? Allocations);
    public sealed record PostCustomerReceiptRequest(Guid FiscalPeriodId, string? EntryNumber, Guid CashAccountId, Guid ReceivableAccountId);
    public sealed record CreateSupplierPaymentRequest(Guid PartyId, string? Number, DateOnly Date, decimal Amount,
        string? CurrencyCode, string? Notes, IReadOnlyList<SettlementAllocationRequest>? Allocations);
    public sealed record PostSupplierPaymentRequest(Guid FiscalPeriodId, string? EntryNumber, Guid CashAccountId, Guid PayableAccountId);
    public sealed record SettlementPostingResult(Guid SettlementId, Guid JournalEntryId, string EntryNumber,
        string JournalEntryStatus, DateOnly EntryDate, decimal Amount, bool AlreadyPosted);
    private sealed record NormalizedAllocation(Guid InvoiceId, decimal Amount);
    private sealed record PartyRow(Guid Id, bool IsActive);
    private sealed record SalesInvoiceBalance(Guid InvoiceId, Guid PartyId, string Status, string CurrencyCode, decimal TotalAmount, decimal Outstanding);
    private sealed record SupplierInvoiceBalance(Guid InvoiceId, Guid PartyId, string Status, string CurrencyCode, decimal TotalAmount, decimal Outstanding);
    private sealed record SettlementAllocationRow(Guid InvoiceId, decimal Amount);
    private sealed record SettlementSource(Guid Id, Guid PartyId, string Number, DateOnly Date, string CurrencyCode,
        decimal Amount, string Status, Guid? JournalEntryId);
    private sealed record CustomerReceiptListRow(Guid Id, string Number, DateOnly Date, Guid PartyId, string PartyName,
        string CurrencyCode, decimal Amount, string Status, Guid? JournalEntryId);
    private sealed record SupplierPaymentListRow(Guid Id, string Number, DateOnly Date, Guid PartyId, string PartyName,
        string CurrencyCode, decimal Amount, string Status, Guid? JournalEntryId);
}

internal sealed class SettlementConflictException(string error) : Exception(error)
{
    public string Error { get; } = error;
}
