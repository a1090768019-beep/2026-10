using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using BusinessERP.Desktop.Services;

namespace BusinessERP.Desktop.ViewModels;

/// <summary>
/// Tenant-scoped item catalog, receipt/issue stock ledger and inter-warehouse transfer workspace.
/// Serial number tracking is supported for inventory receipts, issues and transfers.
/// </summary>
public sealed class InventoryWorkspaceViewModel : INotifyPropertyChanged
{
    private ErpApiClient? _apiClient;
    private ApiCompany? _selectedCompany;
    private InventoryItemApiDto? _selectedItem;
    private InventoryWarehouseApiDto? _selectedWarehouse;
    private InventoryWarehouseApiDto? _selectedSourceWarehouse;
    private InventoryWarehouseApiDto? _selectedDestinationWarehouse;
    private StockBalanceApiRow? _selectedBalance;
    private InventoryMovementApiRow? _selectedLedgerMovement;
    private ApiFiscalPeriod? _selectedLedgerFiscalPeriod;
    private ApiAccount? _selectedInventoryAssetAccount;
    private ApiAccount? _selectedCounterpartAccount;
    private string _ledgerEntryNumber = $"INVGL-{DateTime.Now:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
    private DateTime? _valuationAsOfDate = DateTime.Today;
    private decimal _valuationTotalValue;
    private bool _suppressCompanyReload;
    private bool _isBusy;
    private bool _statusIsError;
    private string _statusMessage = "سجّل الدخول وفعّل المستأجر لإدارة الأصناف والباركود وحركة المخزون.";
    private string _newItemCode = "";
    private string _newBarcode = "";
    private string _newItemNameAr = "";
    private string _newItemNameEn = "";
    private string _newUnitCode = "EA";
    private string _newWarehouseCode = "";
    private string _newWarehouseName = "";
    private string _movementNumber = $"MOV-{DateTime.Now:yyyyMMdd-HHmmss}";
    private decimal _movementQuantity = 1m;
    private decimal? _movementUnitCost;
    private DateTime? _movementDate = DateTime.Today;
    private string _movementNotes = "";
    private string _movementSerialNumbers = "";
    private string _transferSerialNumbers = "";
    private bool _newItemTracksSerials;
    private string _transferNumber = $"TRF-{DateTime.Now:yyyyMMdd-HHmmss}";
    private decimal _transferQuantity = 1m;
    private DateTime? _transferDate = DateTime.Today;
    private string _transferNotes = "";
    private const int PageSize = 100;

    public ObservableCollection<ApiCompany> Companies { get; } = [];
    public ObservableCollection<InventoryItemApiDto> Items { get; } = [];
    public ObservableCollection<InventoryWarehouseApiDto> Warehouses { get; } = [];
    public ObservableCollection<StockBalanceApiRow> StockBalances { get; } = [];
    public ObservableCollection<InventoryValuationApiRow> InventoryValuationRows { get; } = [];
    public ObservableCollection<InventoryMovementApiRow> Movements { get; } = [];
    public ObservableCollection<InventoryTransferApiRow> Transfers { get; } = [];
    public ObservableCollection<InventorySerialEventApiRow> SerialEvents { get; } = [];
    public ObservableCollection<InventorySerialSummaryApiRow> SerialSummaryRows { get; } = [];
    public ObservableCollection<InventoryMovementApiRow> LedgerEligibleMovements { get; } = [];
    public ObservableCollection<ApiAccount> Accounts { get; } = [];
    public ObservableCollection<ApiAccount> InventoryAssetAccounts { get; } = [];
    public ObservableCollection<ApiAccount> CounterpartAccounts { get; } = [];
    public ObservableCollection<ApiFiscalPeriod> OpenFiscalPeriods { get; } = [];

    public ICommand RefreshCommand { get; }
    public ICommand CreateItemCommand { get; }
    public ICommand CreateWarehouseCommand { get; }
    public ICommand ReceiveStockCommand { get; }
    public ICommand IssueStockCommand { get; }
    public ICommand RefreshStockCardCommand { get; }
    public ICommand RefreshInventoryValuationCommand { get; }
    public ICommand TransferStockCommand { get; }
    public ICommand PostInventoryMovementToLedgerCommand { get; }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? WorkspaceContextChanged;

    public InventoryWorkspaceViewModel()
    {
        RefreshCommand = new AsyncCommand(RefreshAsync, () => _apiClient is not null && !IsBusy, HandleUnexpectedFailure);
        CreateItemCommand = new AsyncCommand(CreateItemAsync, CanCreateItem, HandleUnexpectedFailure);
        CreateWarehouseCommand = new AsyncCommand(CreateWarehouseAsync, CanCreateWarehouse, HandleUnexpectedFailure);
        ReceiveStockCommand = new AsyncCommand(() => RecordMovementAsync(receipt: true), CanReceiveStock, HandleUnexpectedFailure);
        IssueStockCommand = new AsyncCommand(() => RecordMovementAsync(receipt: false), CanIssueStock, HandleUnexpectedFailure);
        RefreshStockCardCommand = new AsyncCommand(RefreshStockCardAsync, () => _apiClient is not null && SelectedCompany is not null && !IsBusy, HandleUnexpectedFailure);
        RefreshInventoryValuationCommand = new AsyncCommand(RefreshInventoryValuationAsync, CanRefreshInventoryValuation, HandleUnexpectedFailure);
        TransferStockCommand = new AsyncCommand(RecordTransferAsync, CanTransferStock, HandleUnexpectedFailure);
        PostInventoryMovementToLedgerCommand = new AsyncCommand(PostInventoryMovementToLedgerAsync, CanPostInventoryMovementToLedger, HandleUnexpectedFailure);
    }

    public ApiCompany? SelectedCompany
    {
        get => _selectedCompany;
        set
        {
            if (ReferenceEquals(_selectedCompany, value)) return;
            _selectedCompany = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(WorkspaceContextSummary));
            if (!_suppressCompanyReload && _apiClient is not null) _ = LoadCompanyDataSafelyAsync();
            NotifyCommands();
        }
    }

    public InventoryItemApiDto? SelectedItem
    {
        get => _selectedItem;
        set { if (ReferenceEquals(_selectedItem, value)) return; _selectedItem = value; OnPropertyChanged(); NotifyCommands(); }
    }

    public InventoryWarehouseApiDto? SelectedWarehouse
    {
        get => _selectedWarehouse;
        set { if (ReferenceEquals(_selectedWarehouse, value)) return; _selectedWarehouse = value; OnPropertyChanged(); NotifyCommands(); }
    }

    public InventoryWarehouseApiDto? SelectedSourceWarehouse
    {
        get => _selectedSourceWarehouse;
        set { if (ReferenceEquals(_selectedSourceWarehouse, value)) return; _selectedSourceWarehouse = value; OnPropertyChanged(); NotifyCommands(); }
    }

    public InventoryWarehouseApiDto? SelectedDestinationWarehouse
    {
        get => _selectedDestinationWarehouse;
        set { if (ReferenceEquals(_selectedDestinationWarehouse, value)) return; _selectedDestinationWarehouse = value; OnPropertyChanged(); NotifyCommands(); }
    }

    public InventoryMovementApiRow? SelectedLedgerMovement
    {
        get => _selectedLedgerMovement;
        set
        {
            if (ReferenceEquals(_selectedLedgerMovement, value)) return;
            _selectedLedgerMovement = value;
            OnPropertyChanged();
            RefreshCounterpartAccounts();
            if (value is not null)
            {
                SelectedLedgerFiscalPeriod = OpenFiscalPeriods.FirstOrDefault(period => period.StartDate <= value.MovementDate && period.EndDate >= value.MovementDate)
                    ?? SelectedLedgerFiscalPeriod;
            }
            OnPropertyChanged(nameof(LedgerPostingSummary));
            NotifyCommands();
        }
    }

    public ApiFiscalPeriod? SelectedLedgerFiscalPeriod
    {
        get => _selectedLedgerFiscalPeriod;
        set { if (ReferenceEquals(_selectedLedgerFiscalPeriod, value)) return; _selectedLedgerFiscalPeriod = value; OnPropertyChanged(); NotifyCommands(); }
    }

    public ApiAccount? SelectedInventoryAssetAccount
    {
        get => _selectedInventoryAssetAccount;
        set { if (ReferenceEquals(_selectedInventoryAssetAccount, value)) return; _selectedInventoryAssetAccount = value; OnPropertyChanged(); NotifyCommands(); }
    }

    public ApiAccount? SelectedCounterpartAccount
    {
        get => _selectedCounterpartAccount;
        set { if (ReferenceEquals(_selectedCounterpartAccount, value)) return; _selectedCounterpartAccount = value; OnPropertyChanged(); NotifyCommands(); }
    }

    public string LedgerEntryNumber
    {
        get => _ledgerEntryNumber;
        set { if (_ledgerEntryNumber == value) return; _ledgerEntryNumber = value; OnPropertyChanged(); NotifyCommands(); }
    }

    public string LedgerPostingSummary => SelectedLedgerMovement is null
        ? "اختر حركة استلام/صرف يدوية غير مرحلة."
        : $"{SelectedLedgerMovement.MovementNumber} · {SelectedLedgerMovement.MovementType} · القيمة {Math.Abs(SelectedLedgerMovement.InventoryValueDelta):N2} {SelectedCurrency} · التاريخ {SelectedLedgerMovement.MovementDate:yyyy-MM-dd}";

    public DateTime? ValuationAsOfDate
    {
        get => _valuationAsOfDate;
        set { if (_valuationAsOfDate == value) return; _valuationAsOfDate = value; OnPropertyChanged(); NotifyCommands(); }
    }

    public decimal ValuationTotalValue
    {
        get => _valuationTotalValue;
        private set { if (_valuationTotalValue == value) return; _valuationTotalValue = value; OnPropertyChanged(); OnPropertyChanged(nameof(ValuationSummary)); }
    }

    public string ValuationSummary => $"قيمة المخزون حتى {ValuationAsOfDate:yyyy-MM-dd}: {ValuationTotalValue:N2} {SelectedCurrency} · الصفوف المعروضة {InventoryValuationRows.Count}";

    public StockBalanceApiRow? SelectedBalance
    {
        get => _selectedBalance;
        set
        {
            if (ReferenceEquals(_selectedBalance, value)) return;
            _selectedBalance = value;
            OnPropertyChanged();
            if (value is not null)
            {
                SelectedItem = Items.FirstOrDefault(x => x.Id == value.ItemId) ?? SelectedItem;
                SelectedWarehouse = Warehouses.FirstOrDefault(x => x.Id == value.WarehouseId) ?? SelectedWarehouse;
            }
        }
    }

    public string NewItemCode { get => _newItemCode; set { if (_newItemCode == value) return; _newItemCode = value; OnPropertyChanged(); NotifyCommands(); } }
    public string NewBarcode { get => _newBarcode; set { if (_newBarcode == value) return; _newBarcode = value; OnPropertyChanged(); NotifyCommands(); } }
    public string NewItemNameAr { get => _newItemNameAr; set { if (_newItemNameAr == value) return; _newItemNameAr = value; OnPropertyChanged(); NotifyCommands(); } }
    public string NewItemNameEn { get => _newItemNameEn; set { if (_newItemNameEn == value) return; _newItemNameEn = value; OnPropertyChanged(); NotifyCommands(); } }
    public string NewUnitCode { get => _newUnitCode; set { if (_newUnitCode == value) return; _newUnitCode = value; OnPropertyChanged(); NotifyCommands(); } }
    public string NewWarehouseCode { get => _newWarehouseCode; set { if (_newWarehouseCode == value) return; _newWarehouseCode = value; OnPropertyChanged(); NotifyCommands(); } }
    public string NewWarehouseName { get => _newWarehouseName; set { if (_newWarehouseName == value) return; _newWarehouseName = value; OnPropertyChanged(); NotifyCommands(); } }
    public string MovementNumber { get => _movementNumber; set { if (_movementNumber == value) return; _movementNumber = value; OnPropertyChanged(); NotifyCommands(); } }
    public decimal MovementQuantity { get => _movementQuantity; set { if (_movementQuantity == value) return; _movementQuantity = value; OnPropertyChanged(); NotifyCommands(); } }
    public decimal? MovementUnitCost { get => _movementUnitCost; set { if (_movementUnitCost == value) return; _movementUnitCost = value; OnPropertyChanged(); NotifyCommands(); } }
    public DateTime? MovementDate { get => _movementDate; set { if (_movementDate == value) return; _movementDate = value; OnPropertyChanged(); NotifyCommands(); } }
    public string MovementNotes { get => _movementNotes; set { if (_movementNotes == value) return; _movementNotes = value; OnPropertyChanged(); } }
    public string MovementSerialNumbers { get => _movementSerialNumbers; set { if (_movementSerialNumbers == value) return; _movementSerialNumbers = value; OnPropertyChanged(); } }
    public string TransferSerialNumbers { get => _transferSerialNumbers; set { if (_transferSerialNumbers == value) return; _transferSerialNumbers = value; OnPropertyChanged(); } }
    public bool NewItemTracksSerials { get => _newItemTracksSerials; set { if (_newItemTracksSerials == value) return; _newItemTracksSerials = value; OnPropertyChanged(); NotifyCommands(); } }
    public string TransferNumber { get => _transferNumber; set { if (_transferNumber == value) return; _transferNumber = value; OnPropertyChanged(); NotifyCommands(); } }
    public decimal TransferQuantity { get => _transferQuantity; set { if (_transferQuantity == value) return; _transferQuantity = value; OnPropertyChanged(); NotifyCommands(); } }
    public DateTime? TransferDate { get => _transferDate; set { if (_transferDate == value) return; _transferDate = value; OnPropertyChanged(); NotifyCommands(); } }
    public string TransferNotes { get => _transferNotes; set { if (_transferNotes == value) return; _transferNotes = value; OnPropertyChanged(); } }

    public bool IsBusy
    {
        get => _isBusy;
        private set { if (_isBusy == value) return; _isBusy = value; OnPropertyChanged(); NotifyCommands(); }
    }
    public bool StatusIsError
    {
        get => _statusIsError;
        private set { if (_statusIsError == value) return; _statusIsError = value; OnPropertyChanged(); }
    }
    public string StatusMessage
    {
        get => _statusMessage;
        private set { if (_statusMessage == value) return; _statusMessage = value; OnPropertyChanged(); }
    }
    public string SelectedCurrency => SelectedCompany?.BaseCurrency ?? "SAR";
    public string WorkspaceContextSummary => SelectedCompany is null
        ? "المخزون: الشركة غير محددة"
        : $"المخزون: {SelectedCompany.Name} ({SelectedCurrency}) · الأصناف {Items.Count} · المستودعات {Warehouses.Count} · أرصدة معروضة {StockBalances.Count}";

    public async Task SetApiClientAsync(ErpApiClient? apiClient)
    {
        _apiClient = apiClient;
        if (apiClient is null)
        {
            _suppressCompanyReload = true;
            Companies.Clear();
            SelectedCompany = null;
            _suppressCompanyReload = false;
            ClearCompanyData();
            StatusMessage = "سجّل الدخول وفعّل المستأجر قبل فتح المخزون.";
            StatusIsError = false;
            NotifyCommands();
            return;
        }
        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (_apiClient is null)
        {
            StatusMessage = "سجّل الدخول وفعّل المستأجر أولًا.";
            StatusIsError = true;
            return;
        }
        IsBusy = true;
        StatusIsError = false;
        StatusMessage = "جارٍ تحميل الأصناف والمستودعات والأرصدة...";
        try
        {
            var companies = await _apiClient.GetCompaniesAsync();
            var selectedId = SelectedCompany?.Id;
            _suppressCompanyReload = true;
            Companies.Clear();
            foreach (var company in companies) Companies.Add(company);
            SelectedCompany = (selectedId.HasValue ? Companies.FirstOrDefault(x => x.Id == selectedId.Value) : null)
                ?? Companies.FirstOrDefault();
            _suppressCompanyReload = false;

            if (SelectedCompany is null)
            {
                ClearCompanyData();
                StatusMessage = "لا توجد شركة ضمن المستأجر الحالي.";
                return;
            }
            await LoadCompanyDataAsync();
            StatusMessage = "تم تحديث بيانات الأصناف والمخزون من API.";
        }
        catch (ErpApiException ex) { StatusMessage = ex.Message; StatusIsError = true; }
        catch (HttpRequestException) { StatusMessage = "تعذر الاتصال بالخادم أثناء تحميل بيانات المخزون."; StatusIsError = true; }
        finally { _suppressCompanyReload = false; IsBusy = false; OnPropertyChanged(nameof(WorkspaceContextSummary)); }
    }

    private async Task LoadCompanyDataSafelyAsync()
    {
        if (_apiClient is null || SelectedCompany is null || IsBusy) return;
        IsBusy = true;
        try { await LoadCompanyDataAsync(); }
        catch (ErpApiException ex) { StatusMessage = ex.Message; StatusIsError = true; }
        catch (HttpRequestException) { StatusMessage = "تعذر تحميل المخزون للشركة المحددة."; StatusIsError = true; }
        finally { IsBusy = false; }
    }

    private async Task LoadCompanyDataAsync()
    {
        if (_apiClient is null || SelectedCompany is null) return;
        var companyId = SelectedCompany.Id;
        var itemsTask = _apiClient.GetInventoryItemsAsync(companyId);
        var warehousesTask = _apiClient.GetInventoryWarehousesAsync(companyId);
        var balancesTask = _apiClient.GetInventoryBalancesAsync(companyId, 1, PageSize);
        var movementsTask = _apiClient.GetInventoryMovementsAsync(companyId, page: 1, pageSize: PageSize);
        var transfersTask = _apiClient.GetInventoryTransfersAsync(companyId, page: 1, pageSize: PageSize);
        var serialEventsTask = _apiClient.GetInventorySerialEventsAsync(companyId, page: 1, pageSize: PageSize);
        var serialSummaryTask = _apiClient.GetInventorySerialSummaryAsync(companyId, page: 1, pageSize: PageSize);
        var accountsTask = _apiClient.GetAccountsAsync(companyId);
        var fiscalPeriodsTask = _apiClient.GetFiscalPeriodsAsync(companyId);
        await Task.WhenAll(itemsTask, warehousesTask, balancesTask, movementsTask, transfersTask, serialEventsTask, serialSummaryTask, accountsTask, fiscalPeriodsTask);

        var prevItem = SelectedItem?.Id;
        var prevWarehouse = SelectedWarehouse?.Id;
        Items.Clear();
        foreach (var item in await itemsTask) Items.Add(item);
        Warehouses.Clear();
        foreach (var warehouse in await warehousesTask) Warehouses.Add(warehouse);
        SelectedItem = (prevItem.HasValue ? Items.FirstOrDefault(x => x.Id == prevItem.Value) : null) ?? Items.FirstOrDefault();
        SelectedWarehouse = (prevWarehouse.HasValue ? Warehouses.FirstOrDefault(x => x.Id == prevWarehouse.Value) : null) ?? Warehouses.FirstOrDefault();
        SelectedSourceWarehouse = Warehouses.FirstOrDefault(x => x.Id == _selectedSourceWarehouse?.Id) ?? SelectedWarehouse;
        SelectedDestinationWarehouse = Warehouses.FirstOrDefault(x => x.Id == _selectedDestinationWarehouse?.Id)
            ?? Warehouses.FirstOrDefault(x => x.Id != SelectedSourceWarehouse?.Id);
        StockBalances.Clear();
        foreach (var balance in (await balancesTask).Rows) StockBalances.Add(balance);
        Movements.Clear();
        foreach (var movement in (await movementsTask).Rows) Movements.Add(movement);
        LedgerEligibleMovements.Clear();
        foreach (var movement in Movements.Where(m => m.SourceType == "MANUAL" && !m.TransferId.HasValue &&
                     !m.IsAccountingPosted && (m.MovementType is "RECEIPT" or "ISSUE") &&
                     Math.Abs(m.InventoryValueDelta) > 0m))
            LedgerEligibleMovements.Add(movement);
        Accounts.Clear();
        foreach (var account in await accountsTask) Accounts.Add(account);
        InventoryAssetAccounts.Clear();
        foreach (var account in Accounts.Where(a => a.IsActive && a.AllowPosting &&
                     a.AccountType == "ASSET" && a.NormalBalance == "DEBIT"))
            InventoryAssetAccounts.Add(account);
        OpenFiscalPeriods.Clear();
        foreach (var period in (await fiscalPeriodsTask).Where(p => p.Status == "OPEN"))
            OpenFiscalPeriods.Add(period);
        SelectedInventoryAssetAccount = InventoryAssetAccounts.FirstOrDefault(a => a.Code == "1200") ?? InventoryAssetAccounts.FirstOrDefault();
        SelectedLedgerMovement = LedgerEligibleMovements.FirstOrDefault(m => m.MovementType == "RECEIPT") ?? LedgerEligibleMovements.FirstOrDefault();
        if (SelectedLedgerMovement is not null)
        {
            SelectedLedgerFiscalPeriod = OpenFiscalPeriods.FirstOrDefault(p => p.StartDate <= SelectedLedgerMovement.MovementDate && p.EndDate >= SelectedLedgerMovement.MovementDate)
                ?? OpenFiscalPeriods.FirstOrDefault(p => p.StartDate <= DateOnly.FromDateTime(DateTime.Today) && p.EndDate >= DateOnly.FromDateTime(DateTime.Today))
                ?? OpenFiscalPeriods.FirstOrDefault();
        }
        RefreshCounterpartAccounts();
        Transfers.Clear();
        foreach (var transfer in (await transfersTask).Rows) Transfers.Add(transfer);
        SerialEvents.Clear();
        foreach (var row in (await serialEventsTask).Rows) SerialEvents.Add(row);
        SerialSummaryRows.Clear();
        foreach (var row in (await serialSummaryTask).Rows) SerialSummaryRows.Add(row);
        OnPropertyChanged(nameof(WorkspaceContextSummary));
        OnPropertyChanged(nameof(LedgerPostingSummary));
        NotifyCommands();
    }

    private async Task CreateItemAsync()
    {
        if (!CanCreateItem() || _apiClient is null || SelectedCompany is null) return;
        IsBusy = true;
        try
        {
            var item = await _apiClient.CreateInventoryItemAsync(SelectedCompany.Id,
                new CreateInventoryItemApiRequest(NewItemCode.Trim(), BlankToNull(NewBarcode),
                    NewItemNameAr.Trim(), NewItemNameEn.Trim(), NewUnitCode.Trim(), NewItemTracksSerials));
            await LoadCompanyDataAsync();
            SelectedItem = Items.FirstOrDefault(x => x.Id == item.Id) ?? item;
            NewItemCode = NewBarcode = NewItemNameAr = NewItemNameEn = "";
            NewUnitCode = "EA";
            StatusMessage = $"تم حفظ الصنف {item.ItemCode} والباركود في قاعدة البيانات.";
            StatusIsError = false;
        }
        catch (ErpApiException ex) { StatusMessage = ex.Message; StatusIsError = true; }
        catch (HttpRequestException) { StatusMessage = "تعذر الاتصال بالخادم أثناء حفظ الصنف."; StatusIsError = true; }
        finally { IsBusy = false; }
    }

    private async Task CreateWarehouseAsync()
    {
        if (!CanCreateWarehouse() || _apiClient is null || SelectedCompany is null) return;
        IsBusy = true;
        try
        {
            var warehouse = await _apiClient.CreateInventoryWarehouseAsync(SelectedCompany.Id,
                new CreateInventoryWarehouseApiRequest(NewWarehouseCode.Trim(), NewWarehouseName.Trim()));
            await LoadCompanyDataAsync();
            SelectedWarehouse = Warehouses.FirstOrDefault(x => x.Id == warehouse.Id) ?? warehouse;
            NewWarehouseCode = NewWarehouseName = "";
            StatusMessage = $"تم حفظ المستودع {warehouse.Code}.";
            StatusIsError = false;
        }
        catch (ErpApiException ex) { StatusMessage = ex.Message; StatusIsError = true; }
        catch (HttpRequestException) { StatusMessage = "تعذر الاتصال بالخادم أثناء حفظ المستودع."; StatusIsError = true; }
        finally { IsBusy = false; }
    }

    private async Task RecordMovementAsync(bool receipt)
    {
        if (!(receipt ? CanReceiveStock() : CanIssueStock()) || _apiClient is null || SelectedCompany is null ||
            SelectedItem is null || SelectedWarehouse is null || MovementDate is null) return;
        IsBusy = true;
        try
        {
            var request = new InventoryMovementApiRequest(
                SelectedItem.Id, SelectedWarehouse.Id, MovementNumber.Trim(),
                DateOnly.FromDateTime(MovementDate.Value.Date), MovementQuantity,
                receipt ? MovementUnitCost : null, BlankToNull(MovementNotes), ParseSerialNumbers(MovementSerialNumbers));
            var result = receipt
                ? await _apiClient.ReceiveInventoryStockAsync(SelectedCompany.Id, request)
                : await _apiClient.IssueInventoryStockAsync(SelectedCompany.Id, request);
            await LoadCompanyDataAsync();
            MovementNumber = $"MOV-{DateTime.Now:yyyyMMdd-HHmmss}";
            MovementQuantity = 1m;
            MovementUnitCost = null;
            MovementNotes = "";
            MovementSerialNumbers = "";
            StatusMessage = $"{(receipt ? "تم تسجيل الاستلام" : "تم تسجيل الصرف")} {result.MovementNumber}. الرصيد الحالي {result.OnHandQuantity:N4} {SelectedItem?.UnitCode}؛ القيمة {result.InventoryValue:N4} {SelectedCurrency}؛ متوسط التكلفة {result.AverageUnitCost:N8}.";
            StatusIsError = false;
        }
        catch (ErpApiException ex) { StatusMessage = ex.Message; StatusIsError = true; }
        catch (HttpRequestException) { StatusMessage = "تعذر الاتصال بالخادم أثناء حفظ حركة المخزون."; StatusIsError = true; }
        finally { IsBusy = false; }
    }

    private async Task RecordTransferAsync()
    {
        if (!CanTransferStock() || _apiClient is null || SelectedCompany is null ||
            SelectedItem is null || SelectedSourceWarehouse is null || SelectedDestinationWarehouse is null || TransferDate is null)
            return;

        IsBusy = true;
        try
        {
            var request = new InventoryTransferApiRequest(SelectedItem.Id, SelectedSourceWarehouse.Id,
                SelectedDestinationWarehouse.Id, TransferNumber.Trim(), DateOnly.FromDateTime(TransferDate.Value.Date),
                TransferQuantity, BlankToNull(TransferNotes), ParseSerialNumbers(TransferSerialNumbers));
            var result = await _apiClient.TransferInventoryStockAsync(SelectedCompany.Id, request);
            await LoadCompanyDataAsync();
            TransferNumber = $"TRF-{DateTime.Now:yyyyMMdd-HHmmss}";
            TransferQuantity = 1m;
            TransferNotes = "";
            TransferSerialNumbers = "";
            StatusMessage = $"تم التحويل {result.TransferNumber}: {result.Quantity:N4} {SelectedItem.UnitCode} من {SelectedSourceWarehouse.Name} إلى {SelectedDestinationWarehouse.Name}. القيمة المنقولة {result.InventoryValue:N4} {SelectedCurrency}. رصيد المصدر {result.SourceOnHandQuantity:N4}؛ رصيد الوجهة {result.DestinationOnHandQuantity:N4}.";
            StatusIsError = false;
        }
        catch (ErpApiException ex) { StatusMessage = ex.Message; StatusIsError = true; }
        catch (HttpRequestException) { StatusMessage = "تعذر الاتصال بالخادم أثناء تسجيل التحويل بين المستودعات."; StatusIsError = true; }
        finally { IsBusy = false; }
    }

    private void RefreshCounterpartAccounts()
    {
        var previousId = SelectedCounterpartAccount?.Id;
        CounterpartAccounts.Clear();
        var movementType = SelectedLedgerMovement?.MovementType;
        var candidates = Accounts.Where(account => account.IsActive && account.AllowPosting &&
            (movementType == "RECEIPT"
                ? account.NormalBalance == "CREDIT" && account.AccountType is "LIABILITY" or "EQUITY" or "REVENUE"
                : movementType == "ISSUE" && account.NormalBalance == "DEBIT" && account.AccountType == "EXPENSE"));
        foreach (var account in candidates)
            CounterpartAccounts.Add(account);

        SelectedCounterpartAccount = (previousId.HasValue
            ? CounterpartAccounts.FirstOrDefault(account => account.Id == previousId.Value)
            : null) ?? CounterpartAccounts.FirstOrDefault(account =>
                movementType == "RECEIPT" && account.AccountType == "EQUITY" ||
                movementType == "ISSUE" && account.Code.StartsWith("5", StringComparison.Ordinal))
            ?? CounterpartAccounts.FirstOrDefault();
    }

    private bool CanPostInventoryMovementToLedger() =>
        _apiClient is not null && SelectedCompany is not null && SelectedLedgerMovement is not null &&
        !SelectedLedgerMovement.IsAccountingPosted && SelectedLedgerMovement.SourceType == "MANUAL" &&
        !SelectedLedgerMovement.TransferId.HasValue && (SelectedLedgerMovement.MovementType is "RECEIPT" or "ISSUE") &&
        Math.Abs(SelectedLedgerMovement.InventoryValueDelta) > 0m &&
        SelectedLedgerFiscalPeriod is not null && SelectedLedgerFiscalPeriod.Status == "OPEN" &&
        SelectedLedgerFiscalPeriod.StartDate <= SelectedLedgerMovement.MovementDate &&
        SelectedLedgerFiscalPeriod.EndDate >= SelectedLedgerMovement.MovementDate &&
        SelectedInventoryAssetAccount is not null && SelectedCounterpartAccount is not null &&
        SelectedInventoryAssetAccount.Id != SelectedCounterpartAccount.Id &&
        !string.IsNullOrWhiteSpace(LedgerEntryNumber) && !IsBusy;

    private async Task PostInventoryMovementToLedgerAsync()
    {
        if (!CanPostInventoryMovementToLedger() || _apiClient is null || SelectedCompany is null ||
            SelectedLedgerMovement is null || SelectedLedgerFiscalPeriod is null ||
            SelectedInventoryAssetAccount is null || SelectedCounterpartAccount is null)
            return;

        var movementNumber = SelectedLedgerMovement.MovementNumber;
        IsBusy = true;
        try
        {
            var request = new PostInventoryMovementToLedgerApiRequest(
                SelectedLedgerFiscalPeriod.Id, LedgerEntryNumber.Trim(),
                SelectedInventoryAssetAccount.Id, SelectedCounterpartAccount.Id);
            var result = await _apiClient.PostInventoryMovementToLedgerAsync(
                SelectedCompany.Id, SelectedLedgerMovement.Id, request);
            await LoadCompanyDataAsync();
            LedgerEntryNumber = $"INVGL-{DateTime.Now:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
            StatusMessage = result.AlreadyPosted
                ? $"الحركة {movementNumber} مرحّلة مسبقًا بالقيد {result.EntryNumber}."
                : $"تم ترحيل الحركة {movementNumber} إلى دفتر الأستاذ بالقيد {result.EntryNumber}. القيمة {result.Amount:N2} {SelectedCurrency}. الحالة {result.JournalEntryStatus}.";
            StatusIsError = false;
        }
        catch (ErpApiException ex) { StatusMessage = ex.Message; StatusIsError = true; }
        catch (HttpRequestException) { StatusMessage = "تعذر الاتصال بالخادم أثناء ترحيل حركة المخزون إلى دفتر الأستاذ."; StatusIsError = true; }
        finally { IsBusy = false; NotifyCommands(); }
    }

    private bool CanRefreshInventoryValuation() =>
        _apiClient is not null && SelectedCompany is not null && ValuationAsOfDate.HasValue && !IsBusy;

    private async Task RefreshInventoryValuationAsync()
    {
        if (!CanRefreshInventoryValuation() || _apiClient is null || SelectedCompany is null || !ValuationAsOfDate.HasValue) return;
        IsBusy = true;
        try
        {
            var report = await _apiClient.GetInventoryValuationAsync(SelectedCompany.Id,
                DateOnly.FromDateTime(ValuationAsOfDate.Value), page: 1, pageSize: PageSize);
            InventoryValuationRows.Clear();
            foreach (var row in report.Rows) InventoryValuationRows.Add(row);
            ValuationTotalValue = report.TotalInventoryValue;
            StatusMessage = report.TotalRows > report.Rows.Count
                ? $"تم تحميل {report.Rows.Count} صفًا من أصل {report.TotalRows}. الإجمالي يشمل جميع الصفوف المطابقة حتى التاريخ المحدد."
                : $"تم تحديث تقييم المخزون حتى {report.AsOfDate:yyyy-MM-dd}.";
            StatusIsError = false;
        }
        catch (ErpApiException ex) { StatusMessage = ex.Message; StatusIsError = true; }
        catch (HttpRequestException) { StatusMessage = "تعذر الاتصال بالخادم أثناء حساب تقييم المخزون."; StatusIsError = true; }
        finally { IsBusy = false; NotifyCommands(); }
    }

    private async Task RefreshStockCardAsync()
    {
        if (_apiClient is null || SelectedCompany is null) return;
        IsBusy = true;
        try
        {
            var balances = await _apiClient.GetInventoryBalancesAsync(SelectedCompany.Id, 1, PageSize, SelectedItem?.Id, SelectedWarehouse?.Id);
            var movements = await _apiClient.GetInventoryMovementsAsync(SelectedCompany.Id,
                itemId: SelectedItem?.Id, warehouseId: SelectedWarehouse?.Id, page: 1, pageSize: PageSize);
            StockBalances.Clear();
            foreach (var row in balances.Rows) StockBalances.Add(row);
            Movements.Clear();
            foreach (var movement in movements.Rows) Movements.Add(movement);
            StatusMessage = $"تم تحديث بطاقة المخزون: {Movements.Count} حركة و{StockBalances.Count} رصيد.";
            StatusIsError = false;
        }
        catch (ErpApiException ex) { StatusMessage = ex.Message; StatusIsError = true; }
        catch (HttpRequestException) { StatusMessage = "تعذر تحديث بطاقة المخزون."; StatusIsError = true; }
        finally { IsBusy = false; }
    }

    private bool CanCreateItem() =>
        _apiClient is not null && SelectedCompany is not null && !IsBusy &&
        !string.IsNullOrWhiteSpace(NewItemCode) && !string.IsNullOrWhiteSpace(NewItemNameAr) &&
        !string.IsNullOrWhiteSpace(NewItemNameEn) && !string.IsNullOrWhiteSpace(NewUnitCode);
    private bool CanCreateWarehouse() =>
        _apiClient is not null && SelectedCompany is not null && !IsBusy &&
        !string.IsNullOrWhiteSpace(NewWarehouseCode) && !string.IsNullOrWhiteSpace(NewWarehouseName);
    private bool CanReceiveStock() =>
        CanCreateMovementBase() && MovementUnitCost.HasValue && MovementUnitCost.Value >= 0m &&
        HasPrecision(MovementUnitCost.Value, 4);
    private bool CanIssueStock() => CanCreateMovementBase();
    private bool CanTransferStock() =>
        _apiClient is not null && SelectedCompany is not null && SelectedItem is not null &&
        SelectedSourceWarehouse is not null && SelectedDestinationWarehouse is not null &&
        SelectedSourceWarehouse.Id != SelectedDestinationWarehouse.Id && !IsBusy &&
        !string.IsNullOrWhiteSpace(TransferNumber) && TransferDate.HasValue &&
        TransferQuantity > 0m && HasPrecision(TransferQuantity, 4);

    private bool CanCreateMovementBase() =>
        _apiClient is not null && SelectedCompany is not null && SelectedItem is not null &&
        SelectedWarehouse is not null && !IsBusy && !string.IsNullOrWhiteSpace(MovementNumber) &&
        MovementDate.HasValue && MovementQuantity > 0m && HasPrecision(MovementQuantity, 4);

    private void ClearCompanyData()
    {
        Items.Clear(); Warehouses.Clear(); StockBalances.Clear(); InventoryValuationRows.Clear(); ValuationTotalValue = 0m; Movements.Clear(); Transfers.Clear(); SerialEvents.Clear(); SerialSummaryRows.Clear();
        LedgerEligibleMovements.Clear(); Accounts.Clear(); InventoryAssetAccounts.Clear(); CounterpartAccounts.Clear(); OpenFiscalPeriods.Clear();
        SelectedItem = null; SelectedWarehouse = null; SelectedSourceWarehouse = null; SelectedDestinationWarehouse = null; SelectedBalance = null;
        SelectedLedgerMovement = null; SelectedLedgerFiscalPeriod = null; SelectedInventoryAssetAccount = null; SelectedCounterpartAccount = null;
        OnPropertyChanged(nameof(WorkspaceContextSummary));
    }

    private static string? BlankToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string[] ParseSerialNumbers(string? text) =>
        (text ?? "").Split(new[] { '\r', '\n', ',', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => value.Length > 0).ToArray();
    private static bool HasPrecision(decimal value, int scale) => decimal.Round(value, scale, MidpointRounding.ToEven) == value;

    private void HandleUnexpectedFailure(Exception ex)
    {
        StatusMessage = $"حدث خطأ غير متوقع في المخزون: {ex.Message}";
        StatusIsError = true;
    }

    private void NotifyCommands()
    {
        (RefreshCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        (CreateItemCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        (CreateWarehouseCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        (ReceiveStockCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        (IssueStockCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        (RefreshStockCardCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        (RefreshInventoryValuationCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        (TransferStockCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        (PostInventoryMovementToLedgerCommand as AsyncCommand)?.RaiseCanExecuteChanged();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        if (propertyName is nameof(SelectedCompany) or nameof(Items) or nameof(Warehouses))
            WorkspaceContextChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class AsyncCommand(Func<Task> execute, Func<bool> canExecute, Action<Exception> onFailure) : ICommand
    {
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => canExecute();
        public async void Execute(object? parameter)
        {
            if (!CanExecute(parameter)) return;
            try { await execute(); }
            catch (Exception ex) { onFailure(ex); }
        }
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
