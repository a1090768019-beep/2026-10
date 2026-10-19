using System.Data.Common;
using System.Text.Json;
using BusinessERP.Application.Abstractions;
using BusinessERP.Infrastructure.Persistence;
using BusinessERP.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace BusinessERP.Api.Endpoints;

public static class InventoryMovementLedgerPostingEndpoints
{
    private static readonly string[] AllowedRoles = ["OWNER", "ADMIN", "ACCOUNTANT"];

    public static IEndpointRouteBuilder MapInventoryMovementLedgerPostingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
                "/api/v1/companies/{companyId:guid}/inventory/movements/{movementId:guid}/post-to-ledger",
                PostInventoryMovementToLedger)
            .WithName("PostInventoryMovementToLedger")
            .WithTags("Inventory - Accounting posting");
        return endpoints;
    }

    private static async Task<IResult> PostInventoryMovementToLedger(
        Guid companyId, Guid movementId, PostInventoryMovementToLedgerRequest request,
        ITenantContext tenant, HttpContext http, TenantTransactionRunner runner, CancellationToken ct)
    {
        if (!AllowedRoles.Contains(tenant.RoleCode, StringComparer.OrdinalIgnoreCase))
            return Results.Forbid();

        var entryNumber = (request.EntryNumber ?? "").Trim().ToUpperInvariant();
        if (entryNumber.Length is < 1 or > 80 ||
            entryNumber.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '/' or '.')))
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["entryNumber"] = ["رقم القيد مطلوب ويقبل الأحرف اللاتينية والأرقام والشرطة والنقطة والشرطة السفلية والشرطة المائلة."]
            });

        if (request.FiscalPeriodId == Guid.Empty ||
            request.InventoryAssetAccountId == Guid.Empty || request.CounterpartAccountId == Guid.Empty)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["accounts"] = ["الفترة المالية وحساب المخزون والحساب المقابل مطلوبة."]
            });

        return await runner.ExecuteAsync<IResult>(async (db, token) =>
        {
            var company = await db.Companies.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == companyId && x.TenantId == tenant.TenantId, token);
            if (company is null)
                return Results.NotFound(new { error = "company_not_found" });

            var movementLock = $"inventory-movement-ledger-posting:{tenant.TenantId:D}:{companyId:D}:{movementId:D}";
            await AcquireAdvisoryLockAsync(db, movementLock, token);

            var existingLink = await db.Database.SqlQuery<InventoryMovementPostingLink>($"""
                SELECT posting.id AS "Id",
                       posting.inventory_movement_id AS "InventoryMovementId",
                       posting.journal_entry_id AS "JournalEntryId",
                       posting.inventory_asset_account_id AS "InventoryAssetAccountId",
                       posting.counterpart_account_id AS "CounterpartAccountId",
                       posting.movement_type AS "MovementType",
                       posting.amount AS "Amount",
                       movement.quantity_delta AS "QuantityDelta",
                       posting.posted_at AS "PostedAt"
                  FROM inventory_movement_accounting_postings AS posting
                  JOIN inventory_movements AS movement
                    ON movement.tenant_id = posting.tenant_id
                   AND movement.company_id = posting.company_id
                   AND movement.id = posting.inventory_movement_id
                 WHERE posting.tenant_id = {tenant.TenantId}
                   AND posting.company_id = {companyId}
                   AND posting.inventory_movement_id = {movementId}
                 LIMIT 1
                """).SingleOrDefaultAsync(token);

            if (existingLink is not null)
            {
                var existingEntry = await db.JournalEntries.AsNoTracking().SingleOrDefaultAsync(
                    x => x.Id == existingLink.JournalEntryId && x.TenantId == tenant.TenantId &&
                         x.CompanyId == companyId && x.SourceType == "INVENTORY_MOVEMENT" &&
                         x.SourceId == movementId && x.Status == "POSTED", token);
                if (existingEntry is null)
                    return Results.Problem(statusCode: StatusCodes.Status500InternalServerError,
                        title: "Posted journal entry for this inventory movement was not found");
                return Results.Ok(ToResult(existingLink, existingEntry, alreadyPosted: true));
            }

            var orphanEntry = await db.JournalEntries.AsNoTracking().AnyAsync(
                x => x.TenantId == tenant.TenantId && x.CompanyId == companyId &&
                     x.SourceType == "INVENTORY_MOVEMENT" && x.SourceId == movementId, token);
            if (orphanEntry)
                return Results.Problem(statusCode: StatusCodes.Status500InternalServerError,
                    title: "Inventory movement has a journal but its immutable accounting link is missing");

            var movement = await db.Database.SqlQuery<InventoryMovementPostingSource>($"""
                SELECT id AS "Id",
                       movement_number AS "MovementNumber",
                       movement_type AS "MovementType",
                       movement_date AS "MovementDate",
                       quantity_delta AS "QuantityDelta",
                       inventory_value_delta AS "InventoryValueDelta",
                       source_type AS "SourceType",
                       transfer_id AS "TransferId"
                  FROM inventory_movements
                 WHERE tenant_id = {tenant.TenantId}
                   AND company_id = {companyId}
                   AND id = {movementId}
                 LIMIT 1
                """).SingleOrDefaultAsync(token);

            if (movement is null)
                return Results.NotFound(new { error = "inventory_movement_not_found" });

            if (movement.SourceType != "MANUAL" || movement.TransferId.HasValue ||
                movement.MovementType is not ("RECEIPT" or "ISSUE"))
                return Results.Conflict(new
                {
                    error = "inventory_movement_not_postable",
                    message = "يمكن ترحيل الاستلام والصرف اليدويين فقط. التحويل بين المستودعات لا ينشئ أثرًا ماليًا عند استخدام حساب المخزون نفسه."
                });

            var amount = decimal.Abs(movement.InventoryValueDelta);
            if (amount <= 0m)
                return Results.Conflict(new
                {
                    error = "inventory_movement_has_no_value",
                    message = "لا يمكن ترحيل حركة مخزون بلا قيمة مالية موجبة. راجع تكلفة الحركة أولًا."
                });

            var period = await db.FiscalPeriods.AsNoTracking().SingleOrDefaultAsync(
                x => x.Id == request.FiscalPeriodId && x.TenantId == tenant.TenantId && x.CompanyId == companyId, token);
            if (period is null)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["fiscalPeriodId"] = ["الفترة المالية غير موجودة ضمن الشركة."] });
            if (period.Status != "OPEN" || movement.MovementDate < period.StartDate || movement.MovementDate > period.EndDate)
                return Results.Conflict(new { error = "fiscal_period_not_open_for_inventory_movement_date" });

            if (request.InventoryAssetAccountId == request.CounterpartAccountId)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["accounts"] = ["يجب أن يكون حساب المخزون مختلفًا عن الحساب المقابل."] });

            var requestedAccountIds = new[] { request.InventoryAssetAccountId, request.CounterpartAccountId }.Distinct().ToArray();
            var accounts = await db.Accounts.AsNoTracking()
                .Where(x => x.TenantId == tenant.TenantId && x.CompanyId == companyId && requestedAccountIds.Contains(x.Id))
                .ToListAsync(token);
            if (accounts.Count != requestedAccountIds.Length)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["accounts"] = ["أحد الحسابات غير موجود ضمن الشركة الحالية."] });

            var inventoryAccount = accounts.Single(x => x.Id == request.InventoryAssetAccountId);
            var counterpart = accounts.Single(x => x.Id == request.CounterpartAccountId);
            if (inventoryAccount.AccountType != "ASSET" || inventoryAccount.NormalBalance != "DEBIT" ||
                !inventoryAccount.IsActive || !inventoryAccount.AllowPosting)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["inventoryAssetAccountId"] = ["حساب المخزون يجب أن يكون أصلًا مدينًا ونشطًا وقابلًا للترحيل."] });

            if (movement.MovementType == "RECEIPT")
            {
                if (counterpart.NormalBalance != "CREDIT" ||
                    counterpart.AccountType is not ("LIABILITY" or "EQUITY" or "REVENUE") ||
                    !counterpart.IsActive || !counterpart.AllowPosting)
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["counterpartAccountId"] = ["مقابل الاستلام يجب أن يكون حسابًا دائنًا نشطًا وقابلًا للترحيل من نوع التزام أو حقوق ملكية أو إيراد."] });
            }
            else if (counterpart.AccountType != "EXPENSE" || counterpart.NormalBalance != "DEBIT" ||
                     !counterpart.IsActive || !counterpart.AllowPosting)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["counterpartAccountId"] = ["مقابل الصرف يجب أن يكون حساب مصروف مدينًا ونشطًا وقابلًا للترحيل، مثل تكلفة البضاعة المباعة."] });
            }

            var entryNumberLock = $"journal-entry-number:{tenant.TenantId:D}:{companyId:D}:{entryNumber}";
            await AcquireAdvisoryLockAsync(db, entryNumberLock, token);

            if (await db.JournalEntries.AnyAsync(
                    x => x.TenantId == tenant.TenantId && x.CompanyId == companyId && x.EntryNumber == entryNumber, token))
                return Results.Conflict(new { error = "journal_entry_number_conflict" });

            if (await db.JournalEntries.AnyAsync(
                    x => x.TenantId == tenant.TenantId && x.CompanyId == companyId &&
                         x.SourceType == "INVENTORY_MOVEMENT" && x.SourceId == movementId, token))
                return Results.Conflict(new { error = "inventory_movement_already_has_journal" });

            var now = DateTime.UtcNow;
            var entry = new JournalEntryRecord
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.TenantId,
                CompanyId = companyId,
                FiscalPeriodId = period.Id,
                EntryNumber = entryNumber,
                EntryDate = movement.MovementDate,
                Description = $"Inventory {movement.MovementType.ToLowerInvariant()} {movement.MovementNumber}",
                CurrencyCode = company.BaseCurrency.Trim().ToUpperInvariant(),
                Status = "DRAFT",
                SourceType = "INVENTORY_MOVEMENT",
                SourceId = movementId,
                CreatedAt = now
            };
            db.JournalEntries.Add(entry);
            if (movement.MovementType == "RECEIPT")
            {
                db.JournalEntryLines.Add(new JournalEntryLineRecord
                {
                    Id = Guid.NewGuid(), TenantId = tenant.TenantId, CompanyId = companyId, JournalEntryId = entry.Id,
                    AccountId = inventoryAccount.Id, LineNumber = 1, Description = $"Inventory receipt {movement.MovementNumber}",
                    Debit = amount, Credit = 0m
                });
                db.JournalEntryLines.Add(new JournalEntryLineRecord
                {
                    Id = Guid.NewGuid(), TenantId = tenant.TenantId, CompanyId = companyId, JournalEntryId = entry.Id,
                    AccountId = counterpart.Id, LineNumber = 2, Description = "Inventory receipt counterpart",
                    Debit = 0m, Credit = amount
                });
            }
            else
            {
                db.JournalEntryLines.Add(new JournalEntryLineRecord
                {
                    Id = Guid.NewGuid(), TenantId = tenant.TenantId, CompanyId = companyId, JournalEntryId = entry.Id,
                    AccountId = counterpart.Id, LineNumber = 1, Description = $"Inventory issue cost {movement.MovementNumber}",
                    Debit = amount, Credit = 0m
                });
                db.JournalEntryLines.Add(new JournalEntryLineRecord
                {
                    Id = Guid.NewGuid(), TenantId = tenant.TenantId, CompanyId = companyId, JournalEntryId = entry.Id,
                    AccountId = inventoryAccount.Id, LineNumber = 2, Description = "Inventory asset relief",
                    Debit = 0m, Credit = amount
                });
            }

            await db.SaveChangesAsync(token);
            entry.Status = "APPROVED";
            await db.SaveChangesAsync(token);
            entry.Status = "POSTED";
            entry.PostedAt = now;
            await db.SaveChangesAsync(token);

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO inventory_movement_accounting_postings
                    (tenant_id, company_id, inventory_movement_id, journal_entry_id,
                     inventory_asset_account_id, counterpart_account_id, movement_type, amount, posted_by, posted_at)
                VALUES
                    ({tenant.TenantId}, {companyId}, {movementId}, {entry.Id},
                     {inventoryAccount.Id}, {counterpart.Id}, {movement.MovementType}, {amount}, {tenant.UserId}, {now})
                """, token);

            db.AuditLogs.Add(new AuditLogRecord
            {
                Id = Guid.NewGuid(), TenantId = tenant.TenantId, UserId = tenant.UserId,
                Action = "INVENTORY_MOVEMENT_POSTED_TO_GL",
                EntityType = "InventoryMovement", EntityId = movementId.ToString("D"),
                IpAddress = http.Connection.RemoteIpAddress?.ToString(),
                DetailsJson = JsonSerializer.Serialize(new
                {
                    JournalEntryId = entry.Id, EntryNumber = entry.EntryNumber, movement.MovementNumber,
                    movement.MovementType, Amount = amount, InventoryAssetAccountId = inventoryAccount.Id,
                    CounterpartAccountId = counterpart.Id
                }),
                CreatedAt = now
            });
            await db.SaveChangesAsync(token);

            return Results.Created($"/api/v1/journal-entries/{entry.Id}",
                new InventoryMovementLedgerPostingResult(movementId, entry.Id, entry.EntryNumber, entry.Status,
                    movement.MovementDate, movement.MovementType, movement.QuantityDelta, amount,
                    inventoryAccount.Id, counterpart.Id, false,
                    movement.MovementType == "RECEIPT"
                        ? "تم ترحيل الاستلام اليدوي بقيد مدين للمخزون ودائن للحساب المقابل. هذا المسار لا يربط الاستلام آليًا بفاتورة شراء المورد."
                        : "تم ترحيل الصرف اليدوي بقيد مدين لتكلفة الصرف ودائن للمخزون. هذا المسار لا يربط الصرف آليًا بفاتورة مبيعات."));
        }, ct);
    }

    private static async Task AcquireAdvisoryLockAsync(
        ErpDbContext db, string lockKey, CancellationToken ct)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = "SELECT pg_advisory_xact_lock(hashtextextended(@lock_key, 0))";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "lock_key";
        parameter.Value = lockKey;
        command.Parameters.Add(parameter);
        await command.ExecuteScalarAsync(ct);
    }

    private static InventoryMovementLedgerPostingResult ToResult(
        InventoryMovementPostingLink posting, JournalEntryRecord entry, bool alreadyPosted) =>
        new(posting.InventoryMovementId, entry.Id, entry.EntryNumber, entry.Status, entry.EntryDate,
            posting.MovementType, posting.QuantityDelta, posting.Amount, posting.InventoryAssetAccountId, posting.CounterpartAccountId,
            alreadyPosted, $"الحركة مرتبطة بالفعل بالقيد المرحل {entry.EntryNumber}.");

    public sealed record PostInventoryMovementToLedgerRequest(
        Guid FiscalPeriodId, string? EntryNumber, Guid InventoryAssetAccountId, Guid CounterpartAccountId);

    public sealed record InventoryMovementLedgerPostingResult(
        Guid InventoryMovementId, Guid JournalEntryId, string EntryNumber, string JournalEntryStatus,
        DateOnly EntryDate, string MovementType, decimal QuantityDelta, decimal Amount,
        Guid InventoryAssetAccountId, Guid CounterpartAccountId, bool AlreadyPosted, string Message);

    private sealed class InventoryMovementPostingSource
    {
        public Guid Id { get; init; }
        public string MovementNumber { get; init; } = "";
        public string MovementType { get; init; } = "";
        public DateOnly MovementDate { get; init; }
        public decimal QuantityDelta { get; init; }
        public decimal InventoryValueDelta { get; init; }
        public string SourceType { get; init; } = "";
        public Guid? TransferId { get; init; }
    }

    private sealed class InventoryMovementPostingLink
    {
        public Guid Id { get; init; }
        public Guid InventoryMovementId { get; init; }
        public Guid JournalEntryId { get; init; }
        public Guid InventoryAssetAccountId { get; init; }
        public Guid CounterpartAccountId { get; init; }
        public string MovementType { get; init; } = "";
        public decimal Amount { get; init; }
        public decimal QuantityDelta { get; init; }
        public DateTime PostedAt { get; init; }
    }
}
