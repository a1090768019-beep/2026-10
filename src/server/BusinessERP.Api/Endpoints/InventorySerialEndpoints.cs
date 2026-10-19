using BusinessERP.Application.Abstractions;
using BusinessERP.Infrastructure.Persistence;
using BusinessERP.Infrastructure.Tenancy;

namespace BusinessERP.Api.Endpoints;

public static partial class InventoryEndpoints
{
    private static async Task<IResult> GetSerialEvents(
        Guid companyId, DateOnly? fromDate, DateOnly? toDate, Guid? itemId, Guid? warehouseId,
        string? status, int page, int pageSize, ITenantContext tenant, TenantTransactionRunner runner, CancellationToken ct)
    {
        if (!ValidPage(page, pageSize)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["page"] = ["رقم الصفحة أو حجمها غير صالح."] });
        if (fromDate.HasValue && toDate.HasValue && fromDate > toDate)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["dateRange"] = ["تاريخ البداية يتجاوز النهاية."] });
        var normalizedStatus = string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToUpperInvariant();
        if (normalizedStatus is not null && normalizedStatus is not ("IN_STOCK" or "ISSUED"))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["status"] = ["الحالة غير صالحة."] });

        return await runner.ExecuteAsync<IResult>(async (db, token) =>
        {
            if (!await CompanyExistsAsync(db, tenant.TenantId, companyId, token)) return Results.NotFound();
            await using var command = CreateCommand(db, """
                SELECT event.id, serial.serial_number, item.item_code, item.barcode, item.name_ar,
                       event.event_type, movement.movement_number, movement.movement_type, event.movement_date,
                       event_warehouse.warehouse_code, event_warehouse.name, serial.status,
                       COALESCE(current_warehouse.name, ''), transfer.transfer_number, count(*) OVER() AS total_rows
                  FROM inventory_serial_events event
                  JOIN inventory_serial_numbers serial ON serial.id=event.serial_id AND serial.tenant_id=@tenant_id AND serial.company_id=@company_id
                  JOIN items item ON item.id=event.item_id AND item.tenant_id=@tenant_id AND item.company_id=@company_id
                  JOIN inventory_movements movement ON movement.id=event.movement_id AND movement.tenant_id=@tenant_id AND movement.company_id=@company_id
                  JOIN warehouses event_warehouse ON event_warehouse.id=event.warehouse_id AND event_warehouse.tenant_id=@tenant_id AND event_warehouse.company_id=@company_id
                  LEFT JOIN warehouses current_warehouse ON current_warehouse.id=serial.current_warehouse_id AND current_warehouse.tenant_id=@tenant_id AND current_warehouse.company_id=@company_id
                  LEFT JOIN inventory_transfers transfer ON transfer.id=event.transfer_id AND transfer.tenant_id=@tenant_id AND transfer.company_id=@company_id
                 WHERE event.tenant_id=@tenant_id AND event.company_id=@company_id
                   AND (CAST(@from_date AS date) IS NULL OR event.movement_date >= @from_date)
                   AND (CAST(@to_date AS date) IS NULL OR event.movement_date <= @to_date)
                   AND (CAST(@item_id AS uuid) IS NULL OR event.item_id=@item_id)
                   AND (CAST(@warehouse_id AS uuid) IS NULL OR event.warehouse_id=@warehouse_id)
                   AND (CAST(@status AS varchar) IS NULL OR serial.status=@status)
                 ORDER BY event.movement_date DESC, event.created_at DESC, serial.serial_number, event.event_type
                 OFFSET @offset LIMIT @page_size
                """);
            AddParameter(command,"tenant_id",tenant.TenantId); AddParameter(command,"company_id",companyId);
            AddParameter(command,"from_date",fromDate); AddParameter(command,"to_date",toDate);
            AddParameter(command,"item_id",itemId); AddParameter(command,"warehouse_id",warehouseId);
            AddParameter(command,"status",normalizedStatus); AddParameter(command,"offset",(page-1)*pageSize); AddParameter(command,"page_size",pageSize);
            await using var reader=await command.ExecuteReaderAsync(token);
            var rows=new List<InventorySerialEventRow>(); long total=0;
            while(await reader.ReadAsync(token))
            {
                total=reader.GetInt64(14);
                rows.Add(new InventorySerialEventRow(reader.GetGuid(0),reader.GetString(1),reader.GetString(2),ReadNullableString(reader,3),
                    reader.GetString(4),reader.GetString(5),reader.GetString(6),reader.GetString(7),DateOnly.FromDateTime(reader.GetDateTime(8)),
                    reader.GetString(9),reader.GetString(10),reader.GetString(11),reader.GetString(12),ReadNullableString(reader,13)));
            }
            return Results.Ok(new InventorySerialEventsReport(page,pageSize,total,total==0?0:(int)Math.Ceiling(total/(double)pageSize),rows));
        },ct);
    }

    private static async Task<IResult> GetSerialSummary(Guid companyId, string? status, int page, int pageSize,
        ITenantContext tenant, TenantTransactionRunner runner, CancellationToken ct)
    {
        if (!ValidPage(page,pageSize)) return Results.ValidationProblem(new Dictionary<string,string[]> { ["page"]=["رقم الصفحة أو حجمها غير صالح."] });
        var normalizedStatus=string.IsNullOrWhiteSpace(status)?null:status.Trim().ToUpperInvariant();
        if(normalizedStatus is not null && normalizedStatus is not ("IN_STOCK" or "ISSUED"))
            return Results.ValidationProblem(new Dictionary<string,string[]> { ["status"]=["الحالة غير صالحة."] });

        return await runner.ExecuteAsync<IResult>(async(db,token)=>
        {
            if(!await CompanyExistsAsync(db,tenant.TenantId,companyId,token)) return Results.NotFound();
            await using var command=CreateCommand(db,"""
                WITH serial_groups AS (
                    SELECT item.id AS item_id,item.item_code,item.barcode,item.name_ar,item.name_en,serial.status,
                           serial.current_warehouse_id,COALESCE(warehouse.warehouse_code,'') AS warehouse_code,
                           COALESCE(warehouse.name,'') AS warehouse_name,count(*)::bigint AS serial_count
                      FROM inventory_serial_numbers serial
                      JOIN items item ON item.id=serial.item_id AND item.tenant_id=@tenant_id AND item.company_id=@company_id
                      LEFT JOIN warehouses warehouse ON warehouse.id=serial.current_warehouse_id AND warehouse.tenant_id=@tenant_id AND warehouse.company_id=@company_id
                     WHERE serial.tenant_id=@tenant_id AND serial.company_id=@company_id
                       AND (CAST(@status AS varchar) IS NULL OR serial.status=@status)
                     GROUP BY item.id,item.item_code,item.barcode,item.name_ar,item.name_en,serial.status,
                              serial.current_warehouse_id,warehouse.warehouse_code,warehouse.name
                )
                SELECT serial_groups.*,count(*) OVER() AS total_groups FROM serial_groups
                 ORDER BY item_code,status,warehouse_code OFFSET @offset LIMIT @page_size
                """);
            AddParameter(command,"tenant_id",tenant.TenantId); AddParameter(command,"company_id",companyId);
            AddParameter(command,"status",normalizedStatus); AddParameter(command,"offset",(page-1)*pageSize); AddParameter(command,"page_size",pageSize);
            await using var reader=await command.ExecuteReaderAsync(token);
            var rows=new List<InventorySerialSummaryRow>(); long total=0;
            while(await reader.ReadAsync(token))
            {
                total=reader.GetInt64(10);
                rows.Add(new InventorySerialSummaryRow(reader.GetGuid(0),reader.GetString(1),ReadNullableString(reader,2),
                    reader.GetString(3),reader.GetString(4),reader.GetString(5),reader.IsDBNull(6)?null:reader.GetGuid(6),
                    reader.GetString(7),reader.GetString(8),reader.GetInt64(9)));
            }
            return Results.Ok(new InventorySerialSummaryReport(page,pageSize,total,total==0?0:(int)Math.Ceiling(total/(double)pageSize),rows));
        },ct);
    }

    private static async Task<bool> GetTracksSerialsAsync(ErpDbContext db,Guid tenantId,Guid companyId,Guid itemId,CancellationToken ct)
    {
        await using var command=CreateCommand(db,"SELECT tracks_serials FROM items WHERE tenant_id=@tenant_id AND company_id=@company_id AND id=@item_id");
        AddParameter(command,"tenant_id",tenantId); AddParameter(command,"company_id",companyId); AddParameter(command,"item_id",itemId);
        var value=await command.ExecuteScalarAsync(ct);
        return value is not null && value != DBNull.Value && Convert.ToBoolean(value);
    }

    private static string[] NormalizeSerialNumbers(IReadOnlyList<string>? input)=>(input??Array.Empty<string>())
        .Select(value=>(value??"").Trim().ToUpperInvariant()).Where(value=>value.Length>0).ToArray();

    private static async Task<IResult?> ValidateSerialNumbersAsync(ErpDbContext db,Guid tenantId,Guid companyId,Guid itemId,Guid warehouseId,
        decimal quantity,bool receipt,bool tracksSerials,string[] serialNumbers,CancellationToken ct)
    {
        if(!tracksSerials) return serialNumbers.Length==0?null:Results.ValidationProblem(new Dictionary<string,string[]> { ["serialNumbers"]=["الصنف لا يستخدم تتبع الأرقام التسلسلية."] });
        if(quantity!=decimal.Truncate(quantity)||quantity>1000m||serialNumbers.Length!=(int)quantity)
            return Results.ValidationProblem(new Dictionary<string,string[]> { ["serialNumbers"]=["يجب إدخال رقم تسلسلي فريد لكل وحدة صحيحة، بحد أقصى 1000 وحدة."] });
        if(serialNumbers.Any(s=>s.Length is <1 or >120||s.Any(char.IsControl))||serialNumbers.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=serialNumbers.Length)
            return Results.ValidationProblem(new Dictionary<string,string[]> { ["serialNumbers"]=["الأرقام التسلسلية غير صالحة أو مكررة."] });
        foreach(var serial in serialNumbers.OrderBy(s=>s,StringComparer.Ordinal))
            await AcquireAdvisoryLockAsync(db,$"serial-number:{tenantId:D}:{companyId:D}:{serial}",ct);
        foreach(var serial in serialNumbers)
        {
            await using var command=CreateCommand(db,"SELECT item_id,status,current_warehouse_id FROM inventory_serial_numbers WHERE tenant_id=@tenant_id AND company_id=@company_id AND serial_number=@serial_number");
            AddParameter(command,"tenant_id",tenantId); AddParameter(command,"company_id",companyId); AddParameter(command,"serial_number",serial);
            await using var reader=await command.ExecuteReaderAsync(ct);
            if(!await reader.ReadAsync(ct)){if(!receipt)return Results.Conflict(new {error="serial_not_in_stock",serialNumber=serial}); continue;}
            var oldItem=reader.GetGuid(0); var oldStatus=reader.GetString(1); Guid? oldWarehouse=reader.IsDBNull(2)?null:reader.GetGuid(2);
            if(receipt)
            {
                if(oldItem!=itemId||oldStatus!="ISSUED"||oldWarehouse.HasValue)return Results.Conflict(new {error="serial_already_registered",serialNumber=serial});
            }
            else if(oldItem!=itemId||oldStatus!="IN_STOCK"||oldWarehouse!=warehouseId)
                return Results.Conflict(new {error="serial_not_in_source_warehouse",serialNumber=serial});
        }
        return null;
    }

    private static async Task ApplySerialReceiptAsync(ErpDbContext db,Guid tenantId,Guid companyId,Guid itemId,Guid warehouseId,
        Guid movementId,DateOnly movementDate,string[] serials,Guid userId,CancellationToken ct)
    {
        foreach(var serial in serials)
        {
            Guid id;
            await using(var command=CreateCommand(db,"""
                INSERT INTO inventory_serial_numbers(tenant_id,company_id,item_id,serial_number,status,current_warehouse_id,first_received_movement_id,last_movement_id)
                VALUES(@tenant_id,@company_id,@item_id,@serial,'IN_STOCK',@warehouse,@movement,@movement)
                ON CONFLICT(tenant_id,company_id,serial_number) DO UPDATE SET status='IN_STOCK',current_warehouse_id=EXCLUDED.current_warehouse_id,last_movement_id=EXCLUDED.last_movement_id
                WHERE inventory_serial_numbers.item_id=EXCLUDED.item_id AND inventory_serial_numbers.status='ISSUED' AND inventory_serial_numbers.current_warehouse_id IS NULL
                RETURNING id
                """))
            {
                AddParameter(command,"tenant_id",tenantId); AddParameter(command,"company_id",companyId); AddParameter(command,"item_id",itemId);
                AddParameter(command,"serial",serial); AddParameter(command,"warehouse",warehouseId); AddParameter(command,"movement",movementId);
                await using var reader=await command.ExecuteReaderAsync(ct);
                if(!await reader.ReadAsync(ct))throw new InvalidOperationException($"Serial {serial} became unavailable during receipt.");
                id=reader.GetGuid(0);
            }
            await InsertSerialEventAsync(db,tenantId,companyId,id,itemId,movementId,null,"RECEIPT",warehouseId,movementDate,userId,ct);
        }
    }

    private static async Task ApplySerialIssueAsync(ErpDbContext db,Guid tenantId,Guid companyId,Guid itemId,Guid warehouseId,
        Guid movementId,DateOnly movementDate,string[] serials,Guid userId,CancellationToken ct)
    {
        foreach(var serial in serials)
        {
            Guid id;
            await using(var command=CreateCommand(db,"""
                UPDATE inventory_serial_numbers SET status='ISSUED',current_warehouse_id=NULL,last_movement_id=@movement
                 WHERE tenant_id=@tenant_id AND company_id=@company_id AND item_id=@item_id AND serial_number=@serial
                   AND status='IN_STOCK' AND current_warehouse_id=@warehouse RETURNING id
                """))
            {
                AddParameter(command,"tenant_id",tenantId); AddParameter(command,"company_id",companyId); AddParameter(command,"item_id",itemId);
                AddParameter(command,"serial",serial); AddParameter(command,"warehouse",warehouseId); AddParameter(command,"movement",movementId);
                await using var reader=await command.ExecuteReaderAsync(ct);
                if(!await reader.ReadAsync(ct))throw new InvalidOperationException($"Serial {serial} became unavailable during issue.");
                id=reader.GetGuid(0);
            }
            await InsertSerialEventAsync(db,tenantId,companyId,id,itemId,movementId,null,"ISSUE",warehouseId,movementDate,userId,ct);
        }
    }

    private static async Task ApplySerialTransferAsync(ErpDbContext db,Guid tenantId,Guid companyId,Guid itemId,Guid sourceWarehouseId,
        Guid destinationWarehouseId,Guid transferId,Guid sourceMovementId,Guid destinationMovementId,DateOnly movementDate,string[] serials,
        Guid userId,CancellationToken ct)
    {
        foreach(var serial in serials)
        {
            Guid id;
            await using(var command=CreateCommand(db,"""
                UPDATE inventory_serial_numbers SET current_warehouse_id=@destination,last_movement_id=@destination_movement
                 WHERE tenant_id=@tenant_id AND company_id=@company_id AND item_id=@item_id AND serial_number=@serial
                   AND status='IN_STOCK' AND current_warehouse_id=@source RETURNING id
                """))
            {
                AddParameter(command,"tenant_id",tenantId); AddParameter(command,"company_id",companyId); AddParameter(command,"item_id",itemId);
                AddParameter(command,"serial",serial); AddParameter(command,"source",sourceWarehouseId); AddParameter(command,"destination",destinationWarehouseId);
                AddParameter(command,"destination_movement",destinationMovementId);
                await using var reader=await command.ExecuteReaderAsync(ct);
                if(!await reader.ReadAsync(ct))throw new InvalidOperationException($"Serial {serial} became unavailable during transfer.");
                id=reader.GetGuid(0);
            }
            await InsertSerialEventAsync(db,tenantId,companyId,id,itemId,sourceMovementId,transferId,"TRANSFER_OUT",sourceWarehouseId,movementDate,userId,ct);
            await InsertSerialEventAsync(db,tenantId,companyId,id,itemId,destinationMovementId,transferId,"TRANSFER_IN",destinationWarehouseId,movementDate,userId,ct);
        }
    }

    private static async Task InsertSerialEventAsync(ErpDbContext db,Guid tenantId,Guid companyId,Guid serialId,Guid itemId,Guid movementId,
        Guid? transferId,string eventType,Guid warehouseId,DateOnly movementDate,Guid userId,CancellationToken ct)
    {
        await using var command=CreateCommand(db,"""
            INSERT INTO inventory_serial_events(tenant_id,company_id,serial_id,item_id,movement_id,transfer_id,event_type,warehouse_id,movement_date,created_by)
            VALUES(@tenant_id,@company_id,@serial_id,@item_id,@movement_id,@transfer_id,@event_type,@warehouse_id,@movement_date,@user_id)
            """);
        AddParameter(command,"tenant_id",tenantId); AddParameter(command,"company_id",companyId); AddParameter(command,"serial_id",serialId);
        AddParameter(command,"item_id",itemId); AddParameter(command,"movement_id",movementId); AddParameter(command,"transfer_id",transferId);
        AddParameter(command,"event_type",eventType); AddParameter(command,"warehouse_id",warehouseId); AddParameter(command,"movement_date",movementDate);
        AddParameter(command,"user_id",userId); await command.ExecuteNonQueryAsync(ct);
    }

    private sealed record InventorySerialEventsReport(int Page,int PageSize,long TotalRows,int TotalPages,IReadOnlyList<InventorySerialEventRow> Rows);
    private sealed record InventorySerialEventRow(Guid Id,string SerialNumber,string ItemCode,string? Barcode,string ItemNameAr,string EventType,
        string MovementNumber,string MovementType,DateOnly MovementDate,string WarehouseCode,string WarehouseName,string CurrentStatus,
        string CurrentWarehouseName,string? TransferNumber);
    private sealed record InventorySerialSummaryReport(int Page,int PageSize,long TotalRows,int TotalPages,IReadOnlyList<InventorySerialSummaryRow> Rows);
    private sealed record InventorySerialSummaryRow(Guid ItemId,string ItemCode,string? Barcode,string ItemNameAr,string ItemNameEn,
        string CurrentStatus,Guid? WarehouseId,string WarehouseCode,string WarehouseName,long SerialCount);
}
