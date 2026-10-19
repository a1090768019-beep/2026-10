using System.Data.Common;
using System.Text.Json;
using BusinessERP.Application.Abstractions;
using BusinessERP.Infrastructure.Persistence;
using BusinessERP.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace BusinessERP.Api.Endpoints;

/// <summary>
/// Tenant-scoped item/warehouse endpoints and an immutable receipt/issue inventory ledger.
/// Every command runs inside TenantTransactionRunner's transaction-local RLS context.
/// </summary>
public static class InventoryEndpoints
{
    private static readonly string[] WriteRoles = ["OWNER", "ADMIN", "ACCOUNTANT"];

    public static IEndpointRouteBuilder MapInventoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var company = endpoints.MapGroup("/api/v1/companies/{companyId:guid}").WithTags("Inventory");
        company.MapGet("/items", GetItems).WithName("GetInventoryItems");
        company.MapPost("/items", CreateItem).WithName("CreateInventoryItem");
        company.MapGet("/warehouses", GetWarehouses).WithName("GetInventoryWarehouses");
        company.MapPost("/warehouses", CreateWarehouse).WithName("CreateInventoryWarehouse");
        company.MapGet("/inventory/balances", GetBalances).WithName("GetInventoryBalances");
        company.MapGet("/inventory/movements", GetMovements).WithName("GetInventoryMovements");
        company.MapPost("/inventory/receipts", RecordReceipt).WithName("RecordInventoryReceipt");
        company.MapPost("/inventory/issues", RecordIssue).WithName("RecordInventoryIssue");
        company.MapGet("/inventory/transfers", GetTransfers).WithName("GetInventoryTransfers");
        company.MapPost("/inventory/transfers", RecordTransfer).WithName("RecordInventoryTransfer");
        return endpoints;
    }

    private static async Task<IResult> GetItems(
        Guid companyId, ITenantContext tenant, TenantTransactionRunner runner, CancellationToken ct) =>
        await runner.ExecuteAsync<IResult>(async (db, token) =>
        {
            if (!await CompanyExistsAsync(db, tenant.TenantId, companyId, token)) return Results.NotFound();
            await using var command = CreateCommand(db, """
                SELECT id, item_code, barcode, name_ar, name_en, unit_code, is_active, created_at
                  FROM items
                 WHERE tenant_id = @tenant_id AND company_id = @company_id
                 ORDER BY item_code
                 LIMIT 500
                """);
            AddParameter(command, "tenant_id", tenant.TenantId);
            AddParameter(command, "company_id", companyId);
            await using var reader = await command.ExecuteReaderAsync(token);
            var rows = new List<InventoryItemDto>();
            while (await reader.ReadAsync(token)) rows.Add(ReadItem(reader));
            return Results.Ok(rows);
        }, ct);

    private static async Task<IResult> CreateItem(
        Guid companyId, CreateInventoryItemRequest request, ITenantContext tenant, HttpContext http,
        TenantTransactionRunner runner, CancellationToken ct)
    {
        if (!CanWrite(tenant)) return Results.Forbid();
        var itemCode = request.ItemCode?.Trim().ToUpperInvariant() ?? "";
        var barcode = string.IsNullOrWhiteSpace(request.Barcode) ? null : request.Barcode.Trim();
        var nameAr = request.NameAr?.Trim() ?? "";
        var nameEn = request.NameEn?.Trim() ?? "";
        var unitCode = (request.UnitCode ?? "EA").Trim().ToUpperInvariant();
        var errors = new Dictionary<string, string[]>();
        if (itemCode.Length is < 1 or > 64) errors["itemCode"] = ["رمز الصنف مطلوب ولا يتجاوز 64 حرفًا."];
        if (barcode?.Length > 100) errors["barcode"] = ["الباركود لا يتجاوز 100 حرف."];
        if (nameAr.Length is < 1 or > 200) errors["nameAr"] = ["اسم الصنف بالعربية مطلوب."];
        if (nameEn.Length is < 1 or > 200) errors["nameEn"] = ["اسم الصنف بالإنجليزية مطلوب."];
        if (unitCode.Length is < 1 or > 16) errors["unitCode"] = ["رمز الوحدة مطلوب ولا يتجاوز 16 حرفًا."];
        if (errors.Count != 0) return Results.ValidationProblem(errors);

        try
        {
            return await runner.ExecuteAsync<IResult>(async (db, token) =>
            {
                if (!await CompanyExistsAsync(db, tenant.TenantId, companyId, token)) return Results.NotFound();
                InventoryItemDto item;
                await using (var command = CreateCommand(db, """
                    INSERT INTO items (tenant_id, company_id, item_code, barcode, name_ar, name_en, unit_code)
                    VALUES (@tenant_id, @company_id, @item_code, @barcode, @name_ar, @name_en, @unit_code)
                    RETURNING id, item_code, barcode, name_ar, name_en, unit_code, is_active, created_at
                    """))
                {
                    AddParameter(command, "tenant_id", tenant.TenantId);
                    AddParameter(command, "company_id", companyId);
                    AddParameter(command, "item_code", itemCode);
                    AddParameter(command, "barcode", barcode);
                    AddParameter(command, "name_ar", nameAr);
                    AddParameter(command, "name_en", nameEn);
                    AddParameter(command, "unit_code", unitCode);
                    await using var reader = await command.ExecuteReaderAsync(token);
                    if (!await reader.ReadAsync(token)) throw new InvalidOperationException("The new inventory item was not returned.");
                    item = ReadItem(reader);
                }
                await AddAuditAsync(db, tenant, http, "INVENTORY_ITEM_CREATED", "Item", item.Id.ToString("D"),
                    new { item.ItemCode, item.Barcode, item.NameAr, item.UnitCode }, token);
                return Results.Created($"/api/v1/companies/{companyId:D}/items/{item.Id:D}", item);
            }, ct);
        }
        catch (DbException ex) when (IsUniqueViolation(ex))
        {
            return Results.Conflict(new { error = "duplicate_item_or_barcode", message = "رمز الصنف أو الباركود مستخدم بالفعل داخل الشركة." });
        }
    }

    private static async Task<IResult> GetWarehouses(
        Guid companyId, ITenantContext tenant, TenantTransactionRunner runner, CancellationToken ct) =>
        await runner.ExecuteAsync<IResult>(async (db, token) =>
        {
            if (!await CompanyExistsAsync(db, tenant.TenantId, companyId, token)) return Results.NotFound();
            await using var command = CreateCommand(db, """
                SELECT id, warehouse_code, name, is_active, created_at
                  FROM warehouses
                 WHERE tenant_id = @tenant_id AND company_id = @company_id
                 ORDER BY warehouse_code
                 LIMIT 500
                """);
            AddParameter(command, "tenant_id", tenant.TenantId);
            AddParameter(command, "company_id", companyId);
            await using var reader = await command.ExecuteReaderAsync(token);
            var rows = new List<InventoryWarehouseDto>();
            while (await reader.ReadAsync(token)) rows.Add(ReadWarehouse(reader));
            return Results.Ok(rows);
        }, ct);

    private static async Task<IResult> CreateWarehouse(
        Guid companyId, CreateWarehouseRequest request, ITenantContext tenant, HttpContext http,
        TenantTransactionRunner runner, CancellationToken ct)
    {
        if (!CanWrite(tenant)) return Results.Forbid();
        var code = request.Code?.Trim().ToUpperInvariant() ?? "";
        var name = request.Name?.Trim() ?? "";
        var errors = new Dictionary<string, string[]>();
        if (code.Length is < 1 or > 32) errors["code"] = ["رمز المستودع مطلوب ولا يتجاوز 32 حرفًا."];
        if (name.Length is < 1 or > 200) errors["name"] = ["اسم المستودع مطلوب."];
        if (errors.Count != 0) return Results.ValidationProblem(errors);

        try
        {
            return await runner.ExecuteAsync<IResult>(async (db, token) =>
            {
                if (!await CompanyExistsAsync(db, tenant.TenantId, companyId, token)) return Results.NotFound();
                InventoryWarehouseDto warehouse;
                await using (var command = CreateCommand(db, """
                    INSERT INTO warehouses (tenant_id, company_id, warehouse_code, name)
                    VALUES (@tenant_id, @company_id, @warehouse_code, @name)
                    RETURNING id, warehouse_code, name, is_active, created_at
                    """))
                {
                    AddParameter(command, "tenant_id", tenant.TenantId);
                    AddParameter(command, "company_id", companyId);
                    AddParameter(command, "warehouse_code", code);
                    AddParameter(command, "name", name);
                    await using var reader = await command.ExecuteReaderAsync(token);
                    if (!await reader.ReadAsync(token)) throw new InvalidOperationException("The new warehouse was not returned.");
                    warehouse = ReadWarehouse(reader);
                }
                await AddAuditAsync(db, tenant, http, "INVENTORY_WAREHOUSE_CREATED", "Warehouse", warehouse.Id.ToString("D"),
                    new { warehouse.Code, warehouse.Name }, token);
                return Results.Created($"/api/v1/companies/{companyId:D}/warehouses/{warehouse.Id:D}", warehouse);
            }, ct);
        }
        catch (DbException ex) when (IsUniqueViolation(ex))
        {
            return Results.Conflict(new { error = "warehouse_code_exists", message = "رمز المستودع مستخدم بالفعل داخل الشركة." });
        }
    }


    private static async Task<IResult> GetTransfers(
        Guid companyId, DateOnly? fromDate, DateOnly? toDate, Guid? itemId,
        Guid? sourceWarehouseId, Guid? destinationWarehouseId, int page, int pageSize,
        ITenantContext tenant, TenantTransactionRunner runner, CancellationToken ct)
    {
        if (!ValidPage(page, pageSize))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["page"] = ["رقم الصفحة وحجمها غير صالحين."] });
        if (fromDate.HasValue && toDate.HasValue && fromDate.Value > toDate.Value)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["dateRange"] = ["تاريخ البداية لا يجوز أن يتجاوز تاريخ النهاية."] });

        return await runner.ExecuteAsync<IResult>(async (db, token) =>
        {
            if (!await CompanyExistsAsync(db, tenant.TenantId, companyId, token)) return Results.NotFound();
            await using var command = CreateCommand(db, """
                SELECT transfer.id, transfer.transfer_number, transfer.transfer_date,
                       item.id, item.item_code, item.barcode, item.name_ar, item.unit_code,
                       source.id, source.warehouse_code, source.name,
                       destination.id, destination.warehouse_code, destination.name,
                       transfer.quantity, transfer.unit_cost, transfer.inventory_value,
                       transfer.notes, transfer.created_at, count(*) OVER() AS total_rows
                  FROM inventory_transfers AS transfer
                  JOIN items AS item ON item.id = transfer.item_id
                    AND item.tenant_id = @tenant_id AND item.company_id = @company_id
                  JOIN warehouses AS source ON source.id = transfer.source_warehouse_id
                    AND source.tenant_id = @tenant_id AND source.company_id = @company_id
                  JOIN warehouses AS destination ON destination.id = transfer.destination_warehouse_id
                    AND destination.tenant_id = @tenant_id AND destination.company_id = @company_id
                 WHERE transfer.tenant_id = @tenant_id AND transfer.company_id = @company_id
                   AND (CAST(@from_date AS date) IS NULL OR transfer.transfer_date >= @from_date)
                   AND (CAST(@to_date AS date) IS NULL OR transfer.transfer_date <= @to_date)
                   AND (CAST(@item_id AS uuid) IS NULL OR transfer.item_id = @item_id)
                   AND (CAST(@source_warehouse_id AS uuid) IS NULL OR transfer.source_warehouse_id = @source_warehouse_id)
                   AND (CAST(@destination_warehouse_id AS uuid) IS NULL OR transfer.destination_warehouse_id = @destination_warehouse_id)
                 ORDER BY transfer.transfer_date DESC, transfer.created_at DESC, transfer.transfer_number DESC
                 OFFSET @offset LIMIT @page_size
                """);
            AddParameter(command, "tenant_id", tenant.TenantId);
            AddParameter(command, "company_id", companyId);
            AddParameter(command, "from_date", fromDate);
            AddParameter(command, "to_date", toDate);
            AddParameter(command, "item_id", itemId);
            AddParameter(command, "source_warehouse_id", sourceWarehouseId);
            AddParameter(command, "destination_warehouse_id", destinationWarehouseId);
            AddParameter(command, "offset", (page - 1) * pageSize);
            AddParameter(command, "page_size", pageSize);

            await using var reader = await command.ExecuteReaderAsync(token);
            var rows = new List<InventoryTransferRow>();
            long totalRows = 0;
            while (await reader.ReadAsync(token))
            {
                totalRows = reader.GetInt64(19);
                rows.Add(new InventoryTransferRow(
                    reader.GetGuid(0), reader.GetString(1), DateOnly.FromDateTime(reader.GetDateTime(2)),
                    reader.GetGuid(3), reader.GetString(4), ReadNullableString(reader, 5), reader.GetString(6), reader.GetString(7),
                    reader.GetGuid(8), reader.GetString(9), reader.GetString(10),
                    reader.GetGuid(11), reader.GetString(12), reader.GetString(13),
                    reader.GetDecimal(14), reader.GetDecimal(15), reader.GetDecimal(16),
                    reader.GetString(17), reader.GetDateTime(18)));
            }

            return Results.Ok(new InventoryTransfersReport(page, pageSize, totalRows,
                totalRows == 0 ? 0 : (int)Math.Ceiling(totalRows / (double)pageSize), rows));
        }, ct);
    }

    private static async Task<IResult> RecordTransfer(
        Guid companyId, InventoryTransferRequest request, ITenantContext tenant, HttpContext http,
        TenantTransactionRunner runner, CancellationToken ct)
    {
        if (!CanWrite(tenant)) return Results.Forbid();
        var transferNumber = request.TransferNumber?.Trim().ToUpperInvariant() ?? "";
        var notes = request.Notes?.Trim() ?? "";
        var errors = new Dictionary<string, string[]>();
        if (transferNumber.Length is < 1 or > 72) errors["transferNumber"] = ["رقم التحويل مطلوب ولا يتجاوز 72 حرفًا."];
        if (request.ItemId == Guid.Empty) errors["itemId"] = ["الصنف مطلوب."];
        if (request.SourceWarehouseId == Guid.Empty) errors["sourceWarehouseId"] = ["مستودع المصدر مطلوب."];
        if (request.DestinationWarehouseId == Guid.Empty) errors["destinationWarehouseId"] = ["مستودع الوجهة مطلوب."];
        if (request.SourceWarehouseId == request.DestinationWarehouseId) errors["destinationWarehouseId"] = ["يجب أن يختلف مستودع الوجهة عن مستودع المصدر."];
        if (request.Quantity <= 0 || request.Quantity != decimal.Round(request.Quantity, 4))
            errors["quantity"] = ["الكمية يجب أن تكون أكبر من صفر وبحد أقصى أربع منازل عشرية."];
        if (notes.Length > 1000) errors["notes"] = ["الملاحظات لا تتجاوز 1000 حرف."];
        if (request.TransferDate < new DateOnly(1900, 1, 1) || request.TransferDate > new DateOnly(2200, 12, 31))
            errors["transferDate"] = ["تاريخ التحويل خارج النطاق المدعوم."];
        if (errors.Count != 0) return Results.ValidationProblem(errors);

        try
        {
            return await runner.ExecuteAsync<IResult>(async (db, token) =>
            {
                if (!await CompanyExistsAsync(db, tenant.TenantId, companyId, token)) return Results.NotFound();
                await AcquireAdvisoryLockAsync(db, $"transfer-number:{tenant.TenantId:D}:{companyId:D}:{transferNumber}", token);

                var sourceLock = $"inventory-balance:{tenant.TenantId:D}:{companyId:D}:{request.SourceWarehouseId:D}:{request.ItemId:D}";
                var destinationLock = $"inventory-balance:{tenant.TenantId:D}:{companyId:D}:{request.DestinationWarehouseId:D}:{request.ItemId:D}";
                foreach (var lockKey in new[] { sourceLock, destinationLock }.OrderBy(x => x, StringComparer.Ordinal))
                    await AcquireAdvisoryLockAsync(db, lockKey, token);

                if (!await IsActiveItemAndWarehouseAsync(db, tenant.TenantId, companyId, request.ItemId, request.SourceWarehouseId, token) ||
                    !await IsActiveItemAndWarehouseAsync(db, tenant.TenantId, companyId, request.ItemId, request.DestinationWarehouseId, token))
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["warehouseId"] = ["الصنف أو أحد المستودعين غير موجود أو غير نشط ضمن الشركة المحددة."] });

                await using (var duplicate = CreateCommand(db, """
                    SELECT EXISTS (SELECT 1 FROM inventory_transfers
                                    WHERE tenant_id = @tenant_id AND company_id = @company_id
                                      AND transfer_number = @transfer_number)
                    """))
                {
                    AddParameter(duplicate, "tenant_id", tenant.TenantId);
                    AddParameter(duplicate, "company_id", companyId);
                    AddParameter(duplicate, "transfer_number", transferNumber);
                    if (Convert.ToBoolean(await duplicate.ExecuteScalarAsync(token)))
                        return Results.Conflict(new { error = "transfer_number_exists", message = "رقم التحويل مستخدم مسبقًا داخل الشركة." });
                }

                var (sourceOnHand, sourceValue) = await GetInventoryBalanceAsync(db, tenant.TenantId, companyId,
                    request.SourceWarehouseId, request.ItemId, token);
                var (destinationOnHand, destinationValue) = await GetInventoryBalanceAsync(db, tenant.TenantId, companyId,
                    request.DestinationWarehouseId, request.ItemId, token);
                if (request.Quantity > sourceOnHand)
                    return Results.Conflict(new { error = "insufficient_stock", message = $"الكمية المتاحة في مستودع المصدر {sourceOnHand:N4} أقل من الكمية المطلوبة {request.Quantity:N4}." });

                var transferValue = request.Quantity == sourceOnHand
                    ? sourceValue
                    : decimal.Round(request.Quantity * (sourceValue / sourceOnHand), 4, MidpointRounding.AwayFromZero);
                var unitCost = decimal.Round(transferValue / request.Quantity, 8, MidpointRounding.AwayFromZero);

                Guid transferId;
                DateTime createdAt;
                await using (var header = CreateCommand(db, """
                    INSERT INTO inventory_transfers
                        (tenant_id, company_id, transfer_number, item_id, source_warehouse_id,
                         destination_warehouse_id, transfer_date, quantity, unit_cost, inventory_value, notes, created_by)
                    VALUES
                        (@tenant_id, @company_id, @transfer_number, @item_id, @source_warehouse_id,
                         @destination_warehouse_id, @transfer_date, @quantity, @unit_cost, @inventory_value, @notes, @created_by)
                    RETURNING id, created_at
                    """))
                {
                    AddParameter(header, "tenant_id", tenant.TenantId);
                    AddParameter(header, "company_id", companyId);
                    AddParameter(header, "transfer_number", transferNumber);
                    AddParameter(header, "item_id", request.ItemId);
                    AddParameter(header, "source_warehouse_id", request.SourceWarehouseId);
                    AddParameter(header, "destination_warehouse_id", request.DestinationWarehouseId);
                    AddParameter(header, "transfer_date", request.TransferDate);
                    AddParameter(header, "quantity", request.Quantity);
                    AddParameter(header, "unit_cost", unitCost);
                    AddParameter(header, "inventory_value", transferValue);
                    AddParameter(header, "notes", notes);
                    AddParameter(header, "created_by", tenant.UserId);
                    await using var reader = await header.ExecuteReaderAsync(token);
                    if (!await reader.ReadAsync(token)) throw new InvalidOperationException("The inventory transfer header was not returned.");
                    transferId = reader.GetGuid(0);
                    createdAt = reader.GetDateTime(1);
                }

                await InsertTransferMovementAsync(db, tenant.TenantId, companyId, transferId,
                    transferNumber + "-OUT", request.ItemId, request.SourceWarehouseId, "ISSUE",
                    request.TransferDate, -request.Quantity, unitCost, -transferValue, notes, tenant.UserId, token);
                await InsertTransferMovementAsync(db, tenant.TenantId, companyId, transferId,
                    transferNumber + "-IN", request.ItemId, request.DestinationWarehouseId, "RECEIPT",
                    request.TransferDate, request.Quantity, unitCost, transferValue, notes, tenant.UserId, token);

                await AddAuditAsync(db, tenant, http, "INVENTORY_TRANSFER_RECORDED", "InventoryTransfer",
                    transferId.ToString("D"),
                    new { transferNumber, request.ItemId, request.SourceWarehouseId, request.DestinationWarehouseId,
                        request.TransferDate, request.Quantity, unitCost, transferValue }, token);

                var newSourceQuantity = sourceOnHand - request.Quantity;
                var newSourceValue = sourceValue - transferValue;
                var newDestinationQuantity = destinationOnHand + request.Quantity;
                var newDestinationValue = destinationValue + transferValue;
                return Results.Created($"/api/v1/companies/{companyId:D}/inventory/transfers/{transferId:D}",
                    new InventoryTransferResult(transferId, transferNumber, request.TransferDate, request.ItemId,
                        request.SourceWarehouseId, request.DestinationWarehouseId, request.Quantity, unitCost,
                        transferValue, newSourceQuantity, newSourceValue, newDestinationQuantity, newDestinationValue,
                        newDestinationQuantity == 0 ? 0 : newDestinationValue / newDestinationQuantity, createdAt));
            }, ct);
        }
        catch (DbException ex) when (IsUniqueViolation(ex))
        {
            return Results.Conflict(new { error = "transfer_number_exists", message = "رقم التحويل مستخدم مسبقًا داخل الشركة." });
        }
    }

    private static async Task InsertTransferMovementAsync(
        ErpDbContext db, Guid tenantId, Guid companyId, Guid transferId, string movementNumber,
        Guid itemId, Guid warehouseId, string movementType, DateOnly movementDate, decimal quantityDelta,
        decimal unitCost, decimal valueDelta, string notes, Guid createdBy, CancellationToken ct)
    {
        await using var command = CreateCommand(db, """
            INSERT INTO inventory_movements
                (tenant_id, company_id, movement_number, item_id, warehouse_id, movement_type,
                 movement_date, quantity_delta, unit_cost, inventory_value_delta, source_type, transfer_id, notes, created_by)
            VALUES
                (@tenant_id, @company_id, @movement_number, @item_id, @warehouse_id, @movement_type,
                 @movement_date, @quantity_delta, @unit_cost, @value_delta, 'TRANSFER', @transfer_id, @notes, @created_by)
            """);
        AddParameter(command, "tenant_id", tenantId);
        AddParameter(command, "company_id", companyId);
        AddParameter(command, "movement_number", movementNumber);
        AddParameter(command, "item_id", itemId);
        AddParameter(command, "warehouse_id", warehouseId);
        AddParameter(command, "movement_type", movementType);
        AddParameter(command, "movement_date", movementDate);
        AddParameter(command, "quantity_delta", quantityDelta);
        AddParameter(command, "unit_cost", unitCost);
        AddParameter(command, "value_delta", valueDelta);
        AddParameter(command, "transfer_id", transferId);
        AddParameter(command, "notes", notes);
        AddParameter(command, "created_by", createdBy);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<IResult> GetBalances(
        Guid companyId, int page, int pageSize, Guid? itemId, Guid? warehouseId,
        ITenantContext tenant, TenantTransactionRunner runner, CancellationToken ct)
    {
        if (!ValidPage(page, pageSize)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["page"] = ["رقم الصفحة وحجمها غير صالحين."] });
        return await runner.ExecuteAsync<IResult>(async (db, token) =>
        {
            if (!await CompanyExistsAsync(db, tenant.TenantId, companyId, token)) return Results.NotFound();
            await using var command = CreateCommand(db, """
                WITH balances AS (
                    SELECT movement.item_id, movement.warehouse_id,
                           COALESCE(sum(movement.quantity_delta), 0)::numeric(20,4) AS on_hand_quantity,
                           COALESCE(sum(movement.inventory_value_delta), 0)::numeric(20,4) AS inventory_value
                      FROM inventory_movements AS movement
                     WHERE movement.tenant_id = @tenant_id
                       AND movement.company_id = @company_id
                       AND (CAST(@item_id AS uuid) IS NULL OR movement.item_id = @item_id)
                       AND (CAST(@warehouse_id AS uuid) IS NULL OR movement.warehouse_id = @warehouse_id)
                     GROUP BY movement.item_id, movement.warehouse_id
                    HAVING COALESCE(sum(movement.quantity_delta), 0) <> 0
                        OR COALESCE(sum(movement.inventory_value_delta), 0) <> 0
                ), counted AS (SELECT balances.*, count(*) OVER() AS total_rows FROM balances)
                SELECT i.id, i.item_code, i.barcode, i.name_ar, i.name_en, i.unit_code,
                       w.id, w.warehouse_code, w.name, counted.on_hand_quantity, counted.inventory_value,
                       CASE WHEN counted.on_hand_quantity = 0 THEN 0
                            ELSE counted.inventory_value / counted.on_hand_quantity END AS average_unit_cost,
                       counted.total_rows
                  FROM counted
                  JOIN items AS i ON i.id = counted.item_id AND i.tenant_id = @tenant_id AND i.company_id = @company_id
                  JOIN warehouses AS w ON w.id = counted.warehouse_id AND w.tenant_id = @tenant_id AND w.company_id = @company_id
                 ORDER BY i.item_code, w.warehouse_code
                 OFFSET @offset LIMIT @page_size
                """);
            AddParameter(command, "tenant_id", tenant.TenantId);
            AddParameter(command, "company_id", companyId);
            AddParameter(command, "item_id", itemId);
            AddParameter(command, "warehouse_id", warehouseId);
            AddParameter(command, "offset", (page - 1) * pageSize);
            AddParameter(command, "page_size", pageSize);
            await using var reader = await command.ExecuteReaderAsync(token);
            var rows = new List<InventoryBalanceRow>();
            long totalRows = 0;
            while (await reader.ReadAsync(token))
            {
                totalRows = reader.GetInt64(12);
                rows.Add(new InventoryBalanceRow(reader.GetGuid(0), reader.GetString(1), ReadNullableString(reader, 2),
                    reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetGuid(6),
                    reader.GetString(7), reader.GetString(8), reader.GetDecimal(9), reader.GetDecimal(10), reader.GetDecimal(11)));
            }
            return Results.Ok(new InventoryBalancesReport(page, pageSize, totalRows,
                totalRows == 0 ? 0 : (int)Math.Ceiling(totalRows / (double)pageSize), rows));
        }, ct);
    }

    private static async Task<IResult> GetMovements(
        Guid companyId, DateOnly? fromDate, DateOnly? toDate, Guid? itemId, Guid? warehouseId,
        int page, int pageSize, ITenantContext tenant, TenantTransactionRunner runner, CancellationToken ct)
    {
        if (!ValidPage(page, pageSize)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["page"] = ["رقم الصفحة وحجمها غير صالحين."] });
        if (fromDate.HasValue && toDate.HasValue && fromDate.Value > toDate.Value)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["dateRange"] = ["تاريخ البداية لا يجوز أن يتجاوز تاريخ النهاية."] });

        return await runner.ExecuteAsync<IResult>(async (db, token) =>
        {
            if (!await CompanyExistsAsync(db, tenant.TenantId, companyId, token)) return Results.NotFound();
            await using var command = CreateCommand(db, """
                SELECT movement.id, movement.movement_number, movement.movement_type, movement.movement_date,
                       i.id, i.item_code, i.barcode, i.name_ar,
                       w.id, w.warehouse_code, w.name,
                       movement.quantity_delta, movement.unit_cost, movement.inventory_value_delta,
                       movement.notes, movement.created_at, count(*) OVER() AS total_rows
                  FROM inventory_movements AS movement
                  JOIN items AS i ON i.id = movement.item_id AND i.tenant_id = @tenant_id AND i.company_id = @company_id
                  JOIN warehouses AS w ON w.id = movement.warehouse_id AND w.tenant_id = @tenant_id AND w.company_id = @company_id
                 WHERE movement.tenant_id = @tenant_id AND movement.company_id = @company_id
                   AND (CAST(@from_date AS date) IS NULL OR movement.movement_date >= @from_date)
                   AND (CAST(@to_date AS date) IS NULL OR movement.movement_date <= @to_date)
                   AND (CAST(@item_id AS uuid) IS NULL OR movement.item_id = @item_id)
                   AND (CAST(@warehouse_id AS uuid) IS NULL OR movement.warehouse_id = @warehouse_id)
                 ORDER BY movement.movement_date DESC, movement.created_at DESC, movement.movement_number DESC
                 OFFSET @offset LIMIT @page_size
                """);
            AddParameter(command, "tenant_id", tenant.TenantId);
            AddParameter(command, "company_id", companyId);
            AddParameter(command, "from_date", fromDate);
            AddParameter(command, "to_date", toDate);
            AddParameter(command, "item_id", itemId);
            AddParameter(command, "warehouse_id", warehouseId);
            AddParameter(command, "offset", (page - 1) * pageSize);
            AddParameter(command, "page_size", pageSize);
            await using var reader = await command.ExecuteReaderAsync(token);
            var rows = new List<InventoryMovementRow>();
            long totalRows = 0;
            while (await reader.ReadAsync(token))
            {
                totalRows = reader.GetInt64(16);
                rows.Add(new InventoryMovementRow(
                    reader.GetGuid(0), reader.GetString(1), reader.GetString(2), DateOnly.FromDateTime(reader.GetDateTime(3)),
                    reader.GetGuid(4), reader.GetString(5), ReadNullableString(reader, 6), reader.GetString(7),
                    reader.GetGuid(8), reader.GetString(9), reader.GetString(10), reader.GetDecimal(11),
                    reader.GetDecimal(12), reader.GetDecimal(13), reader.GetString(14), reader.GetDateTime(15)));
            }
            return Results.Ok(new InventoryMovementsReport(page, pageSize, totalRows,
                totalRows == 0 ? 0 : (int)Math.Ceiling(totalRows / (double)pageSize), rows));
        }, ct);
    }

    private static Task<IResult> RecordReceipt(Guid companyId, InventoryMovementRequest request, ITenantContext tenant,
        HttpContext http, TenantTransactionRunner runner, CancellationToken ct) =>
        RecordMovementAsync(companyId, request, true, tenant, http, runner, ct);

    private static Task<IResult> RecordIssue(Guid companyId, InventoryMovementRequest request, ITenantContext tenant,
        HttpContext http, TenantTransactionRunner runner, CancellationToken ct) =>
        RecordMovementAsync(companyId, request, false, tenant, http, runner, ct);

    private static async Task<IResult> RecordMovementAsync(
        Guid companyId, InventoryMovementRequest request, bool receipt, ITenantContext tenant,
        HttpContext http, TenantTransactionRunner runner, CancellationToken ct)
    {
        if (!CanWrite(tenant)) return Results.Forbid();
        var movementNumber = request.MovementNumber?.Trim().ToUpperInvariant() ?? "";
        var notes = request.Notes?.Trim() ?? "";
        var errors = new Dictionary<string, string[]>();
        if (movementNumber.Length is < 1 or > 80) errors["movementNumber"] = ["رقم الحركة مطلوب ولا يتجاوز 80 حرفًا."];
        if (request.ItemId == Guid.Empty) errors["itemId"] = ["الصنف مطلوب."];
        if (request.WarehouseId == Guid.Empty) errors["warehouseId"] = ["المستودع مطلوب."];
        if (request.Quantity <= 0 || request.Quantity != decimal.Round(request.Quantity, 4))
            errors["quantity"] = ["الكمية يجب أن تكون أكبر من صفر وبحد أقصى أربع منازل عشرية."];
        if (receipt && (!request.UnitCost.HasValue || request.UnitCost.Value < 0 ||
                        request.UnitCost.Value != decimal.Round(request.UnitCost.Value, 8)))
            errors["unitCost"] = ["تكلفة الوحدة مطلوبة للاستلام ويجب أن تكون غير سالبة وبحد أقصى 8 منازل عشرية."];
        if (notes.Length > 1000) errors["notes"] = ["الملاحظات لا تتجاوز 1000 حرف."];
        if (request.MovementDate < new DateOnly(1900, 1, 1) || request.MovementDate > new DateOnly(2200, 12, 31))
            errors["movementDate"] = ["تاريخ الحركة خارج النطاق المدعوم."];
        if (errors.Count != 0) return Results.ValidationProblem(errors);

        try
        {
            return await runner.ExecuteAsync<IResult>(async (db, token) =>
            {
                if (!await CompanyExistsAsync(db, tenant.TenantId, companyId, token)) return Results.NotFound();
                await AcquireAdvisoryLockAsync(db, $"movement-number:{tenant.TenantId:D}:{companyId:D}:{movementNumber}", token);
                await AcquireAdvisoryLockAsync(db, $"inventory-balance:{tenant.TenantId:D}:{companyId:D}:{request.WarehouseId:D}:{request.ItemId:D}", token);

                if (!await IsActiveItemAndWarehouseAsync(db, tenant.TenantId, companyId, request.ItemId, request.WarehouseId, token))
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["itemId"] = ["الصنف أو المستودع غير موجود أو غير نشط ضمن الشركة المحددة."] });

                await using (var duplicate = CreateCommand(db, """
                    SELECT EXISTS (SELECT 1 FROM inventory_movements
                                    WHERE tenant_id = @tenant_id AND company_id = @company_id AND movement_number = @movement_number)
                    """))
                {
                    AddParameter(duplicate, "tenant_id", tenant.TenantId);
                    AddParameter(duplicate, "company_id", companyId);
                    AddParameter(duplicate, "movement_number", movementNumber);
                    if (Convert.ToBoolean(await duplicate.ExecuteScalarAsync(token)))
                        return Results.Conflict(new { error = "movement_number_exists", message = "رقم الحركة مستخدم مسبقًا داخل الشركة." });
                }

                var (onHand, currentValue) = await GetInventoryBalanceAsync(db, tenant.TenantId, companyId,
                    request.WarehouseId, request.ItemId, token);
                decimal unitCost, quantityDelta, valueDelta;
                if (receipt)
                {
                    unitCost = request.UnitCost!.Value;
                    quantityDelta = request.Quantity;
                    valueDelta = decimal.Round(request.Quantity * unitCost, 4, MidpointRounding.AwayFromZero);
                }
                else
                {
                    if (request.Quantity > onHand)
                        return Results.Conflict(new { error = "insufficient_stock", message = $"الكمية المتاحة {onHand:N4} أقل من الكمية المطلوبة {request.Quantity:N4}." });
                    unitCost = onHand == 0 ? 0 : currentValue / onHand;
                    var issueValue = request.Quantity == onHand ? currentValue :
                        decimal.Round(request.Quantity * unitCost, 4, MidpointRounding.AwayFromZero);
                    if (issueValue > currentValue)
                        return Results.Conflict(new { error = "insufficient_stock", message = "تعذر صرف الكمية المطلوبة دون تجاوز قيمة المخزون المتاحة." });
                    quantityDelta = -request.Quantity;
                    valueDelta = -issueValue;
                }

                Guid movementId;
                DateTime createdAt;
                await using (var insert = CreateCommand(db, """
                    INSERT INTO inventory_movements
                        (tenant_id, company_id, movement_number, item_id, warehouse_id, movement_type,
                         movement_date, quantity_delta, unit_cost, inventory_value_delta, source_type, notes, created_by)
                    VALUES
                        (@tenant_id, @company_id, @movement_number, @item_id, @warehouse_id, @movement_type,
                         @movement_date, @quantity_delta, @unit_cost, @inventory_value_delta, 'MANUAL', @notes, @created_by)
                    RETURNING id, created_at
                    """))
                {
                    AddParameter(insert, "tenant_id", tenant.TenantId);
                    AddParameter(insert, "company_id", companyId);
                    AddParameter(insert, "movement_number", movementNumber);
                    AddParameter(insert, "item_id", request.ItemId);
                    AddParameter(insert, "warehouse_id", request.WarehouseId);
                    AddParameter(insert, "movement_type", receipt ? "RECEIPT" : "ISSUE");
                    AddParameter(insert, "movement_date", request.MovementDate);
                    AddParameter(insert, "quantity_delta", quantityDelta);
                    AddParameter(insert, "unit_cost", unitCost);
                    AddParameter(insert, "inventory_value_delta", valueDelta);
                    AddParameter(insert, "notes", notes);
                    AddParameter(insert, "created_by", tenant.UserId);
                    await using var reader = await insert.ExecuteReaderAsync(token);
                    if (!await reader.ReadAsync(token)) throw new InvalidOperationException("The new inventory movement was not returned.");
                    movementId = reader.GetGuid(0);
                    createdAt = reader.GetDateTime(1);
                }

                await AddAuditAsync(db, tenant, http, receipt ? "INVENTORY_RECEIPT_RECORDED" : "INVENTORY_ISSUE_RECORDED",
                    "InventoryMovement", movementId.ToString("D"),
                    new { movementNumber, request.ItemId, request.WarehouseId, request.MovementDate, quantityDelta, unitCost, valueDelta }, token);

                var newQuantity = onHand + quantityDelta;
                var newValue = currentValue + valueDelta;
                var result = new InventoryMovementResult(movementId, movementNumber, receipt ? "RECEIPT" : "ISSUE",
                    request.ItemId, request.WarehouseId, request.MovementDate, quantityDelta, unitCost, valueDelta,
                    newQuantity, newValue, newQuantity == 0 ? 0 : newValue / newQuantity);
                return Results.Created($"/api/v1/companies/{companyId:D}/inventory/movements/{movementId:D}", result);
            }, ct);
        }
        catch (DbException ex) when (IsUniqueViolation(ex))
        {
            return Results.Conflict(new { error = "movement_number_exists", message = "رقم الحركة مستخدم مسبقًا داخل الشركة." });
        }
    }

    private static async Task<bool> CompanyExistsAsync(ErpDbContext db, Guid tenantId, Guid companyId, CancellationToken ct)
    {
        await using var command = CreateCommand(db, "SELECT EXISTS (SELECT 1 FROM companies WHERE tenant_id = @tenant_id AND id = @company_id)");
        AddParameter(command, "tenant_id", tenantId);
        AddParameter(command, "company_id", companyId);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(ct));
    }

    private static async Task<bool> IsActiveItemAndWarehouseAsync(
        ErpDbContext db, Guid tenantId, Guid companyId, Guid itemId, Guid warehouseId, CancellationToken ct)
    {
        await using var command = CreateCommand(db, """
            SELECT
                EXISTS (SELECT 1 FROM items WHERE tenant_id=@tenant_id AND company_id=@company_id AND id=@item_id AND is_active)
                AND
                EXISTS (SELECT 1 FROM warehouses WHERE tenant_id=@tenant_id AND company_id=@company_id AND id=@warehouse_id AND is_active)
            """);
        AddParameter(command, "tenant_id", tenantId);
        AddParameter(command, "company_id", companyId);
        AddParameter(command, "item_id", itemId);
        AddParameter(command, "warehouse_id", warehouseId);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(ct));
    }

    private static async Task<(decimal Quantity, decimal InventoryValue)> GetInventoryBalanceAsync(
        ErpDbContext db, Guid tenantId, Guid companyId, Guid warehouseId, Guid itemId, CancellationToken ct)
    {
        await using var command = CreateCommand(db, """
            SELECT COALESCE(sum(movement.quantity_delta), 0),
                   COALESCE(sum(movement.inventory_value_delta), 0)
              FROM inventory_movements AS movement
             WHERE movement.tenant_id = @tenant_id AND movement.company_id = @company_id
               AND movement.warehouse_id = @warehouse_id AND movement.item_id = @item_id
            """);
        AddParameter(command, "tenant_id", tenantId);
        AddParameter(command, "company_id", companyId);
        AddParameter(command, "warehouse_id", warehouseId);
        AddParameter(command, "item_id", itemId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return (0m, 0m);
        return (reader.GetDecimal(0), reader.GetDecimal(1));
    }

    private static async Task AcquireAdvisoryLockAsync(ErpDbContext db, string lockKey, CancellationToken ct)
    {
        await using var command = CreateCommand(db, "SELECT pg_advisory_xact_lock(hashtextextended(@lock_key, 0))");
        AddParameter(command, "lock_key", lockKey);
        await command.ExecuteScalarAsync(ct);
    }

    private static async Task AddAuditAsync(
        ErpDbContext db, ITenantContext tenant, HttpContext http, string action,
        string entityType, string entityId, object details, CancellationToken ct)
    {
        await using var command = CreateCommand(db, """
            INSERT INTO audit_logs (tenant_id, user_id, action, entity_type, entity_id, ip_address, details_json)
            VALUES (@tenant_id, @user_id, @action, @entity_type, @entity_id, @ip_address, CAST(@details_json AS jsonb))
            """);
        AddParameter(command, "tenant_id", tenant.TenantId);
        AddParameter(command, "user_id", tenant.UserId);
        AddParameter(command, "action", action);
        AddParameter(command, "entity_type", entityType);
        AddParameter(command, "entity_id", entityId);
        AddParameter(command, "ip_address", http.Connection.RemoteIpAddress?.ToString());
        AddParameter(command, "details_json", JsonSerializer.Serialize(details));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static DbCommand CreateCommand(ErpDbContext db, string sql)
    {
        var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = sql;
        return command;
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static bool ValidPage(int page, int pageSize) => page is >= 1 and <= 100_000 && pageSize is >= 1 and <= 200;
    private static bool CanWrite(ITenantContext tenant) => WriteRoles.Contains(tenant.RoleCode, StringComparer.OrdinalIgnoreCase);
    private static bool IsUniqueViolation(DbException exception) =>
        string.Equals(exception.GetType().GetProperty("SqlState")?.GetValue(exception)?.ToString(), "23505", StringComparison.Ordinal);

    private static InventoryItemDto ReadItem(DbDataReader reader) =>
        new(reader.GetGuid(0), reader.GetString(1), ReadNullableString(reader, 2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), reader.GetBoolean(6), reader.GetDateTime(7));

    private static InventoryWarehouseDto ReadWarehouse(DbDataReader reader) =>
        new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), reader.GetDateTime(4));

    private static string? ReadNullableString(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private sealed record InventoryItemDto(Guid Id, string ItemCode, string? Barcode, string NameAr, string NameEn,
        string UnitCode, bool IsActive, DateTime CreatedAt);
    private sealed record InventoryWarehouseDto(Guid Id, string Code, string Name, bool IsActive, DateTime CreatedAt);
    private sealed record InventoryMovementResult(Guid Id, string MovementNumber, string MovementType, Guid ItemId,
        Guid WarehouseId, DateOnly MovementDate, decimal QuantityDelta, decimal UnitCost, decimal InventoryValueDelta,
        decimal OnHandQuantity, decimal InventoryValue, decimal AverageUnitCost);
    private sealed record InventoryBalancesReport(int Page, int PageSize, long TotalRows, int TotalPages, IReadOnlyList<InventoryBalanceRow> Rows);
    private sealed record InventoryBalanceRow(Guid ItemId, string ItemCode, string? Barcode, string ItemNameAr, string ItemNameEn,
        string UnitCode, Guid WarehouseId, string WarehouseCode, string WarehouseName, decimal OnHandQuantity,
        decimal InventoryValue, decimal AverageUnitCost);
    private sealed record InventoryMovementsReport(int Page, int PageSize, long TotalRows, int TotalPages, IReadOnlyList<InventoryMovementRow> Rows);
    private sealed record InventoryMovementRow(Guid Id, string MovementNumber, string MovementType, DateOnly MovementDate,
        Guid ItemId, string ItemCode, string? Barcode, string ItemNameAr, Guid WarehouseId, string WarehouseCode,
        string WarehouseName, decimal QuantityDelta, decimal UnitCost, decimal InventoryValueDelta, string Notes, DateTime CreatedAt);
    private sealed record CreateInventoryItemRequest(string? ItemCode, string? Barcode, string? NameAr, string? NameEn, string? UnitCode = "EA");
    private sealed record CreateWarehouseRequest(string? Code, string? Name);
    private sealed record InventoryMovementRequest(Guid ItemId, Guid WarehouseId, string? MovementNumber, DateOnly MovementDate,
        decimal Quantity, decimal? UnitCost = null, string? Notes = null);
    private sealed record InventoryTransferRequest(Guid ItemId, Guid SourceWarehouseId, Guid DestinationWarehouseId,
        string? TransferNumber, DateOnly TransferDate, decimal Quantity, string? Notes = null);
    private sealed record InventoryTransferResult(Guid Id, string TransferNumber, DateOnly TransferDate, Guid ItemId,
        Guid SourceWarehouseId, Guid DestinationWarehouseId, decimal Quantity, decimal UnitCost, decimal InventoryValue,
        decimal SourceOnHandQuantity, decimal SourceInventoryValue, decimal DestinationOnHandQuantity,
        decimal DestinationInventoryValue, decimal DestinationAverageUnitCost, DateTime CreatedAt);
    private sealed record InventoryTransfersReport(int Page, int PageSize, long TotalRows, int TotalPages,
        IReadOnlyList<InventoryTransferRow> Rows);
    private sealed record InventoryTransferRow(Guid Id, string TransferNumber, DateOnly TransferDate,
        Guid ItemId, string ItemCode, string? Barcode, string ItemNameAr, string UnitCode,
        Guid SourceWarehouseId, string SourceWarehouseCode, string SourceWarehouseName,
        Guid DestinationWarehouseId, string DestinationWarehouseCode, string DestinationWarehouseName,
        decimal Quantity, decimal UnitCost, decimal InventoryValue, string Notes, DateTime CreatedAt);
}
