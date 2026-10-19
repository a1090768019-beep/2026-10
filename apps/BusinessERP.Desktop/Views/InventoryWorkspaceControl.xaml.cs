using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using System.Xml;
using BusinessERP.Desktop.Services;
using BusinessERP.Desktop.ViewModels;

namespace BusinessERP.Desktop.Views;

public partial class InventoryWorkspaceControl : UserControl
{
    private readonly InventoryWorkspaceViewModel _viewModel = new();

    public InventoryWorkspaceControl()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.WorkspaceContextChanged += (_, _) => WorkspaceContextChanged?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? WorkspaceContextChanged;
    public string WorkspaceContextSummary => _viewModel.WorkspaceContextSummary;
    public string WorkspaceStatusMessage => _viewModel.StatusMessage;

    public Task SetApiClientAsync(ErpApiClient? apiClient) => _viewModel.SetApiClientAsync(apiClient);
    public Task RefreshAsync() => _viewModel.RefreshAsync();

    // Export only the currently loaded page; the current API request is capped at 100 rows.
    private void ExportStockBalances_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedCompany is null || _viewModel.StockBalances.Count == 0)
        {
            MessageBox.Show(Window.GetWindow(this), "لا توجد أرصدة مخزنية محمّلة للتصدير.", "Business ERP", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var reportRows = new List<object?[]>
        {
            new object?[] { "Business ERP", "الأرصدة المخزنية والقيمة بالتكلفة المتوسطة" },
            new object?[] { "الشركة", _viewModel.SelectedCompany.Name, "العملة", _viewModel.SelectedCurrency },
            new object?[] { "ملاحظة", "هذه البيانات للصفحة المحمّلة حاليًا فقط وليست كشفًا شاملًا لكل الصفحات." },
            Array.Empty<object?>(),
            new object?[] { "رمز الصنف", "الباركود", "اسم الصنف", "اسم الصنف بالإنجليزية", "الوحدة", "رمز المستودع", "اسم المستودع", "الكمية المتاحة", "متوسط التكلفة", "القيمة" }
        };
        reportRows.AddRange(_viewModel.StockBalances.Select(row => new object?[]
        {
            row.ItemCode, row.Barcode, row.ItemNameAr, row.ItemNameEn, row.UnitCode,
            row.WarehouseCode, row.WarehouseName, row.OnHandQuantity, row.AverageUnitCost, row.InventoryValue
        }));

        SaveInventoryReport("BusinessERP-InventoryBalances", "InventoryBalances", reportRows);
    }

    private void ExportInventoryMovements_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedCompany is null || _viewModel.Movements.Count == 0)
        {
            MessageBox.Show(Window.GetWindow(this), "لا توجد حركات مخزنية محمّلة للتصدير.", "Business ERP", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var reportRows = new List<object?[]>
        {
            new object?[] { "Business ERP", "بطاقة حركة المخزون" },
            new object?[] { "الشركة", _viewModel.SelectedCompany.Name, "العملة", _viewModel.SelectedCurrency },
            new object?[] { "ملاحظة", "هذه البيانات للصفحة المحمّلة حاليًا فقط وليست كشفًا شاملًا لكل الصفحات." },
            Array.Empty<object?>(),
            new object?[] { "رقم الحركة", "نوع الحركة", "التاريخ", "رمز الصنف", "الباركود", "الصنف", "المستودع", "الكمية الموقعة", "تكلفة الوحدة", "تغير القيمة", "الملاحظات" }
        };
        reportRows.AddRange(_viewModel.Movements.Select(row => new object?[]
        {
            row.MovementNumber, row.MovementType, row.MovementDate, row.ItemCode, row.Barcode,
            row.ItemNameAr, row.WarehouseName, row.QuantityDelta, row.UnitCost, row.InventoryValueDelta, row.Notes
        }));

        SaveInventoryReport("BusinessERP-InventoryMovements", "InventoryMovements", reportRows);
    }

    private void ExportInventoryTransfers_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedCompany is null || _viewModel.Transfers.Count == 0)
        {
            MessageBox.Show(Window.GetWindow(this), "لا توجد تحويلات مخزنية محمّلة للتصدير.", "Business ERP", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var rows = new List<object?[]>
        {
            new object?[] { "Business ERP", "سجل التحويلات المخزنية" },
            new object?[] { "الشركة", _viewModel.SelectedCompany.Name, "العملة", _viewModel.SelectedCurrency },
            new object?[] { "ملاحظة", "هذه البيانات للصفحة المحمّلة حاليًا فقط وليست كشفًا شاملًا لكل الصفحات." },
            Array.Empty<object?>(),
            new object?[] { "رقم التحويل", "التاريخ", "الصنف", "رمز الصنف", "الباركود", "من مستودع", "رمز المصدر", "إلى مستودع", "رمز الوجهة", "الكمية", "تكلفة الوحدة", "القيمة", "ملاحظات" }
        };
        rows.AddRange(_viewModel.Transfers.Select(row => new object?[]
        {
            row.TransferNumber, row.TransferDate, row.ItemNameAr, row.ItemCode, row.Barcode,
            row.SourceWarehouseName, row.SourceWarehouseCode, row.DestinationWarehouseName, row.DestinationWarehouseCode,
            row.Quantity, row.UnitCost, row.InventoryValue, row.Notes
        }));
        SaveInventoryReport("BusinessERP-InventoryTransfers", "InventoryTransfers", rows);
    }

    private void ExportInventorySerialEvents_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SerialEvents.Count == 0)
        {
            MessageBox.Show(Window.GetWindow(this), "لا توجد حركة أرقام تسلسلية محمّلة للتصدير.", "Business ERP", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var rows = new List<object?[]>
        {
            new object?[] { "Business ERP", "سجل الأرقام التسلسلية" },
            new object?[] { "ملاحظة", "الملف يعرض الصفحة المحمّلة حاليًا فقط (حتى 100 صف)." },
            Array.Empty<object?>(),
            new object?[] { "الرقم التسلسلي", "رمز الصنف", "الباركود", "الصنف", "نوع الحدث", "رقم الحركة", "نوع الحركة", "التاريخ", "رمز المستودع", "المستودع وقت الحركة", "الحالة الحالية", "الموقع الحالي", "رقم التحويل" }
        };
        rows.AddRange(_viewModel.SerialEvents.Select(row => new object?[]
        {
            row.SerialNumber, row.ItemCode, row.Barcode, row.ItemNameAr, row.EventType, row.MovementNumber,
            row.MovementType, row.MovementDate, row.WarehouseCode, row.WarehouseName, row.CurrentStatus,
            row.CurrentWarehouseName, row.TransferNumber
        }));
        SaveInventoryReport("BusinessERP-InventorySerialEvents", "SerialEvents", rows);
    }

    private void ExportInventorySerialSummary_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SerialSummaryRows.Count == 0)
        {
            MessageBox.Show(Window.GetWindow(this), "لا يوجد ملخص أرقام تسلسلية محمّل للتصدير.", "Business ERP", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var rows = new List<object?[]>
        {
            new object?[] { "Business ERP", "ملخص الأرقام التسلسلية" },
            new object?[] { "ملاحظة", "الملف يعرض الصفحة المحمّلة حاليًا فقط (حتى 100 صف)." },
            Array.Empty<object?>(),
            new object?[] { "رمز الصنف", "الباركود", "اسم الصنف", "الاسم الإنجليزي", "الحالة", "رمز المستودع", "المستودع", "عدد الأرقام" }
        };
        rows.AddRange(_viewModel.SerialSummaryRows.Select(row => new object?[]
        {
            row.ItemCode, row.Barcode, row.ItemNameAr, row.ItemNameEn, row.CurrentStatus,
            row.WarehouseCode, row.WarehouseName, row.SerialCount
        }));
        SaveInventoryReport("BusinessERP-InventorySerialSummary", "SerialSummary", rows);
    }

    private void SaveInventoryReport(string filePrefix, string sheetName, IReadOnlyList<object?[]> rows)
    {
        var dialog = new SaveFileDialog
        {
            Title = "تصدير تقرير المخزون إلى Excel",
            Filter = "Excel Workbook (*.xlsx)|*.xlsx",
            DefaultExt = ".xlsx",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = $"{filePrefix}-{DateTime.Now:yyyyMMdd-HHmm}.xlsx"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;

        try
        {
            MinimalXlsxWriter.Write(dialog.FileName, sheetName, rows);
            MessageBox.Show(Window.GetWindow(this), $"تم تصدير الصفحة المحمّلة إلى {Path.GetFileName(dialog.FileName)}.", "Business ERP", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or XmlException)
        {
            MessageBox.Show(Window.GetWindow(this), "تعذر إنشاء ملف Excel. تحقق من صلاحية الكتابة والمساحة المتاحة ثم أعد المحاولة.", "Business ERP", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

}
