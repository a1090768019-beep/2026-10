using System.Data.Common;
using System.Text.Json;
using BusinessERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace BusinessERP.Api.Endpoints;

internal sealed record InventoryDocumentPostingLine(
    Guid Id,
    int LineNumber,
    Guid? InventoryItemId,
    Guid? InventoryWarehouseId,
    decimal Quantity,
    decimal LineAmount,
    decimal? NetAmount,
    decimal? TotalAmount,
    string SerialNumbersJson,
    decimal? InventoryCostAmount);

internal sealed record InventoryStockBalance(decimal Quantity, decimal Value);

internal sealed record InventoryIssuePlan(
    InventoryDocumentPostingLine Line,
    Guid ItemId,
    Guid WarehouseId,
    decimal UnitCost,
    decimal CostAmount,
    string[] SerialNumbers);

internal static class InventoryDocumentStockPostingService
{
    public static string[] ParseSerialNumbers(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return InventoryEndpoints.NormalizeSerialNumbers(JsonSerializer.Deserialize<string[]>(json) ?? []);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Serial numbers saved on the inventory line are not valid JSON.");
        }
    }

    public static async Task AcquireMovementLocksAsync(
        ErpDbContext db, Guid tenantId, Guid companyId, IEnumerable<(Guid ItemId, Guid WarehouseId)> movements,
        CancellationToken ct)
    {
        var lockKeys = movements
            .Select(x => $"inventory-balance:{tenantId:D}:{companyId:D}:{x.WarehouseId:D}:{x.ItemId:D}")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal);
        foreach (var key in lockKeys)
            await AcquireAdvisoryLockAsync(db, key, ct);
    }

    public static async Task<InventoryStockBalance> GetBalanceAsync(
        ErpDbContext db, Guid tenantId, Guid companyId, Guid warehouseId, Guid itemId, CancellationToken ct)
    {
        return await db.Database.SqlQuery<InventoryStockBalance>($"""
            SELECT COALESCE(sum(quantity_delta), 0)::numeric(20,4) AS "Quantity",
                   COALESCE(sum(inventory_value_delta), 0)::numeric(20,4) AS "Value"
              FROM inventory_movements
             WHERE tenant_id = {tenantId} AND company_id = {companyId}
               AND warehouse_id = {warehouseId} AND item_id = {itemId}
            """).SingleAsync(ct);
    }

    public static async Task<Guid> InsertDocumentMovementAsync(
        ErpDbContext db, Guid tenantId, Guid companyId, string sourceType, Guid sourceLineId,
        Guid itemId, Guid warehouseId, DateOnly movementDate, decimal quantityDelta,
        decimal unitCost, decimal valueDelta, string notes, Guid createdBy, CancellationToken ct)
    {
        if (sourceType is not ("PURCHASE" or "SALES"))
            throw new ArgumentOutOfRangeException(nameof(sourceType), "Document stock source must be PURCHASE or SALES.");
        var movementId = Guid.NewGuid();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = """
            INSERT INTO inventory_movements
                (id, tenant_id, company_id, movement_number, item_id, warehouse_id,
                 movement_type, movement_date, quantity_delta, unit_cost, inventory_value_delta,
                 source_type, transfer_id, purchase_invoice_draft_line_id, sales_invoice_draft_line_id,
                 notes, created_by)
            VALUES
                (@id, @tenant_id, @company_id, @movement_number, @item_id, @warehouse_id,
                 @movement_type, @movement_date, @quantity_delta, @unit_cost, @value_delta,
                 @source_type, NULL, @purchase_line_id, @sales_line_id, @notes, @created_by)
            RETURNING id
            """;
        var receipt = sourceType == "PURCHASE";
        Add(command, "id", movementId);
        Add(command, "tenant_id", tenantId);
        Add(command, "company_id", companyId);
        Add(command, "movement_number", $"{(receipt ? "PUR" : "SAL")}-{sourceLineId:N}".ToUpperInvariant());
        Add(command, "item_id", itemId);
        Add(command, "warehouse_id", warehouseId);
        Add(command, "movement_type", receipt ? "RECEIPT" : "ISSUE");
        Add(command, "movement_date", movementDate);
        Add(command, "quantity_delta", quantityDelta);
        Add(command, "unit_cost", unitCost);
        Add(command, "value_delta", valueDelta);
        Add(command, "source_type", sourceType);
        Add(command, "purchase_line_id", receipt ? sourceLineId : null);
        Add(command, "sales_line_id", receipt ? null : sourceLineId);
        Add(command, "notes", notes.Length <= 1000 ? notes : notes[..1000]);
        Add(command, "created_by", createdBy);
        var insertedId = await command.ExecuteScalarAsync(ct);
        return insertedId is Guid id
            ? id
            : throw new InvalidOperationException("The document-linked inventory movement ID was not returned.");
    }

    public static async Task ValidateSerialLineAsync(
        ErpDbContext db, Guid tenantId, Guid companyId, Guid itemId, Guid warehouseId, decimal quantity,
        bool receipt, string[] serialNumbers, CancellationToken ct)
    {
        var tracksSerials = await InventoryEndpoints.GetTracksSerialsAsync(db, tenantId, companyId, itemId, ct);
        var validation = await InventoryEndpoints.ValidateSerialNumbersAsync(
            db, tenantId, companyId, itemId, warehouseId, quantity, receipt, tracksSerials, serialNumbers, ct);
        if (validation is not null)
            throw new InventoryDocumentValidationException(validation);
    }

    public static async Task RecordSerialEventsAsync(
        ErpDbContext db, Guid tenantId, Guid companyId, Guid itemId, Guid warehouseId, Guid movementId,
        DateOnly movementDate, decimal quantity, bool receipt, string[] serialNumbers, Guid createdBy,
        CancellationToken ct)
    {
        if (serialNumbers.Length == 0) return;
        if (receipt)
            await InventoryEndpoints.ApplySerialReceiptAsync(db, tenantId, companyId, itemId, warehouseId,
                movementId, movementDate, serialNumbers, createdBy, ct);
        else
            await InventoryEndpoints.ApplySerialIssueAsync(db, tenantId, companyId, itemId, warehouseId,
                movementId, movementDate, serialNumbers, createdBy, ct);
    }

    private static async Task AcquireAdvisoryLockAsync(ErpDbContext db, string lockKey, CancellationToken ct)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = "SELECT pg_advisory_xact_lock(hashtextextended(@lock_key, 0))";
        Add(command, "lock_key", lockKey);
        await command.ExecuteScalarAsync(ct);
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}

internal sealed class InventoryDocumentValidationException(IResult result) : Exception("Inventory document line validation failed.")
{
    public IResult Result { get; } = result;
}
