using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using BusinessERP.Desktop.Services;

namespace BusinessERP.Desktop.ViewModels;

/// <summary>
/// Tenant-scoped item catalog and immutable receipt/issue stock ledger workspace.
/// Serial-level tracking, inter-warehouse transfers, and purchase/sales document integration are not yet enabled.
/// </summary>
public sealed class InventoryWorkspaceViewModel : INotifyPropertyChanged
{
    private ErpApiClient? _apiClient;
    private ApiCompany? _selectedCompany;
    private InventoryItemApiDto? _selectedItem;
    private InventoryWarehouseApiDto? _selectedWarehouse;
    private StockBalanceApiRow? _selectedBalance;
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
    private const int PageSize = 100;

    public ObservableCollection<ApiCompany> Companies { get; } = [];
    public ObservableCollection<InventoryItemApiDto> Items { get; } = [];
    public ObservableCollection<InventoryWarehouseApiDto> Warehouses { get; } = [];
    public ObservableCollection<StockBalanceApiRow> StockBalances { get; } = [];
    public ObservableCollection<InventoryMovementApiRow> Movements { get; } = [];

    public ICommand RefreshCommand { get; }
    public ICommand CreateItemCommand { get; }
    public ICommand CreateWarehouseCommand { get; }
    public ICommand ReceiveStockCommand { get; }
    public ICommand IssueStockCommand { get; }
    public ICommand RefreshStockCardCommand { get; }

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
        await Task.WhenAll(itemsTask, warehousesTask, balancesTask, movementsTask);

        var prevItem = SelectedItem?.Id;
        var prevWarehouse = SelectedWarehouse?.Id;
        Items.Clear();
        foreach (var item in await itemsTask) Items.Add(item);
        Warehouses.Clear();
        foreach (var warehouse in await warehousesTask) Warehouses.Add(warehouse);
        SelectedItem = (prevItem.HasValue ? Items.FirstOrDefault(x => x.Id == prevItem.Value) : null) ?? Items.FirstOrDefault();
        SelectedWarehouse = (prevWarehouse.HasValue ? Warehouses.FirstOrDefault(x => x.Id == prevWarehouse.Value) : null) ?? Warehouses.FirstOrDefault();
        StockBalances.Clear();
        foreach (var balance in (await balancesTask).Rows) StockBalances.Add(balance);
        Movements.Clear();
        foreach (var movement in (await movementsTask).Rows) Movements.Add(movement);
        OnPropertyChanged(nameof(WorkspaceContextSummary));
    }

    private async Task CreateItemAsync()
    {
        if (!CanCreateItem() || _apiClient is null || SelectedCompany is null) return;
        IsBusy = true;
        try
        {
            var item = await _apiClient.CreateInventoryItemAsync(SelectedCompany.Id,
                new CreateInventoryItemApiRequest(NewItemCode.Trim(), BlankToNull(NewBarcode),
                    NewItemNameAr.Trim(), NewItemNameEn.Trim(), NewUnitCode.Trim()));
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
                receipt ? MovementUnitCost : null, BlankToNull(MovementNotes));
            var result = receipt
                ? await _apiClient.ReceiveInventoryStockAsync(SelectedCompany.Id, request)
                : await _apiClient.IssueInventoryStockAsync(SelectedCompany.Id, request);
            await LoadCompanyDataAsync();
            MovementNumber = $"MOV-{DateTime.Now:yyyyMMdd-HHmmss}";
            MovementQuantity = 1m;
            MovementUnitCost = null;
            MovementNotes = "";
            StatusMessage = $"{(receipt ? "تم تسجيل الاستلام" : "تم تسجيل الصرف")} {result.MovementNumber}. الرصيد الحالي {result.OnHandQuantity:N4} {SelectedItem?.UnitCode}؛ القيمة {result.InventoryValue:N4} {SelectedCurrency}؛ متوسط التكلفة {result.AverageUnitCost:N8}.";
            StatusIsError = false;
        }
        catch (ErpApiException ex) { StatusMessage = ex.Message; StatusIsError = true; }
        catch (HttpRequestException) { StatusMessage = "تعذر الاتصال بالخادم أثناء حفظ حركة المخزون."; StatusIsError = true; }
        finally { IsBusy = false; }
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

    private bool CanCreateMovementBase() =>
        _apiClient is not null && SelectedCompany is not null && SelectedItem is not null &&
        SelectedWarehouse is not null && !IsBusy && !string.IsNullOrWhiteSpace(MovementNumber) &&
        MovementDate.HasValue && MovementQuantity > 0m && HasPrecision(MovementQuantity, 4);

    private void ClearCompanyData()
    {
        Items.Clear(); Warehouses.Clear(); StockBalances.Clear(); Movements.Clear();
        SelectedItem = null; SelectedWarehouse = null; SelectedBalance = null;
        OnPropertyChanged(nameof(WorkspaceContextSummary));
    }

    private static string? BlankToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
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
