using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using BusinessERP.Desktop.Services;
using BusinessERP.Desktop.ViewModels;

namespace BusinessERP.Desktop.Views;

public partial class ReportsWorkspaceControl : UserControl
{
    private readonly ReportsWorkspaceViewModel _viewModel = new();

    public ReportsWorkspaceControl()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    public event EventHandler? WorkspaceContextChanged;

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ReportsWorkspaceViewModel.WorkspaceContextSummary))
            WorkspaceContextChanged?.Invoke(this, EventArgs.Empty);
    }

    public string WorkspaceContextSummary => _viewModel.WorkspaceContextSummary;

    public Task SetApiClientAsync(ErpApiClient? apiClient) => _viewModel.SetApiClientAsync(apiClient);

    public Task RefreshAsync() => _viewModel.RefreshAsync();

    public string WorkspaceStatusMessage => _viewModel.StatusMessage;

    public string? ExportCurrentReportToExcel()
    {
        var report = BuildCurrentReport();
        if (report is null)
            return "حمّل التقرير أولًا قبل التصدير.";

        var dialog = new SaveFileDialog
        {
            Title = "تصدير التقرير إلى Excel",
            Filter = "Excel Workbook (*.xlsx)|*.xlsx",
            DefaultExt = ".xlsx",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = $"BusinessERP-{report.Value.SheetName}-{DateTime.Now:yyyyMMdd-HHmm}.xlsx"
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return null;

        try
        {
            MinimalXlsxWriter.Write(dialog.FileName, report.Value.SheetName, report.Value.Rows);
            return $"تم تصدير التقرير المحدد إلى Excel: {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or XmlException)
        {
            return "تعذر إنشاء ملف Excel. تحقق من صلاحية الكتابة والمساحة المتاحة ثم أعد المحاولة.";
        }
    }

    private (string SheetName, IReadOnlyList<object?[]> Rows)? BuildCurrentReport()
    {
        if (ReportsTabs.SelectedIndex == 0 && _viewModel.CurrentTrialBalance is { } trial)
        {
            var rows = new List<object?[]>
            {
                new object?[] { "Business ERP", "ميزان المراجعة" },
                new object?[] { "الشركة", _viewModel.SelectedCompany?.Name ?? "" },
                new object?[] { "الفترة", $"{trial.FromDate:yyyy-MM-dd} — {trial.ToDate:yyyy-MM-dd}" },
                Array.Empty<object?>(),
                new object?[] { "رمز الحساب", "الحساب", "Account name", "النوع", "حركة مدين", "حركة دائن", "رصيد مدين", "رصيد دائن" }
            };
            rows.AddRange(trial.Rows.Select(x => new object?[]
            {
                x.AccountCode, x.AccountNameAr, x.AccountNameEn, x.AccountType,
                x.DebitMovement, x.CreditMovement, x.DebitBalance, x.CreditBalance
            }));
            rows.Add(Array.Empty<object?>());
            rows.Add(new object?[] { "الإجمالي", "", "", "",
                trial.TotalDebitMovement, trial.TotalCreditMovement, trial.TotalDebitBalance, trial.TotalCreditBalance });
            return ("TrialBalance", rows);
        }

        if (ReportsTabs.SelectedIndex == 1 && _viewModel.CurrentIncomeStatement is { } income)
        {
            var rows = new List<object?[]>
            {
                new object?[] { "Business ERP", "قائمة الدخل" },
                new object?[] { "الشركة", _viewModel.SelectedCompany?.Name ?? "" },
                new object?[] { "الفترة", $"{income.FromDate:yyyy-MM-dd} — {income.ToDate:yyyy-MM-dd}" },
                Array.Empty<object?>(),
                new object?[] { "الفئة", "رمز الحساب", "الحساب", "Account name", "المبلغ" }
            };
            rows.AddRange(income.Revenue.Select(x => new object?[] { "إيراد", x.AccountCode, x.AccountNameAr, x.AccountNameEn, x.Amount }));
            rows.AddRange(income.Expenses.Select(x => new object?[] { "مصروف", x.AccountCode, x.AccountNameAr, x.AccountNameEn, x.Amount }));
            rows.Add(Array.Empty<object?>());
            rows.Add(new object?[] { "إجمالي الإيرادات", "", "", "", income.TotalRevenue });
            rows.Add(new object?[] { "إجمالي المصروفات", "", "", "", income.TotalExpenses });
            rows.Add(new object?[] { "صافي الدخل", "", "", "", income.NetIncome });
            return ("IncomeStatement", rows);
        }

        if (ReportsTabs.SelectedIndex == 2 && _viewModel.CurrentBalanceSheet is { } balance)
        {
            var rows = new List<object?[]>
            {
                new object?[] { "Business ERP", "الميزانية العمومية" },
                new object?[] { "الشركة", _viewModel.SelectedCompany?.Name ?? "" },
                new object?[] { "حتى تاريخ", balance.AsOfDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
                Array.Empty<object?>(),
                new object?[] { "الفئة", "رمز الحساب", "الحساب", "Account name", "المبلغ" }
            };
            rows.AddRange(balance.Assets.Select(x => new object?[] { "أصول", x.AccountCode, x.AccountNameAr, x.AccountNameEn, x.Amount }));
            rows.AddRange(balance.Liabilities.Select(x => new object?[] { "التزامات", x.AccountCode, x.AccountNameAr, x.AccountNameEn, x.Amount }));
            rows.AddRange(balance.Equity.Select(x => new object?[] { "حقوق ملكية", x.AccountCode, x.AccountNameAr, x.AccountNameEn, x.Amount }));
            rows.Add(Array.Empty<object?>());
            rows.Add(new object?[] { "إجمالي الأصول", "", "", "", balance.TotalAssets });
            rows.Add(new object?[] { "إجمالي الالتزامات", "", "", "", balance.TotalLiabilities });
            rows.Add(new object?[] { "إجمالي حقوق الملكية", "", "", "", balance.TotalEquity });
            rows.Add(new object?[] { "الأرباح غير المقفلة", "", "", "", balance.UnclosedEarnings });
            rows.Add(new object?[] { "فرق المعادلة", "", "", "", balance.Difference });
            return ("BalanceSheet", rows);
        }

        return null;
    }
}

/// <summary>
/// Writes a minimal Open XML workbook without formulas or external package dependencies.
/// All labels are emitted as inline strings and all numeric report values as numeric cells.
/// </summary>
internal static class MinimalXlsxWriter
{
    private const string SpreadsheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string OfficeRelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PackageRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string ContentTypeNs = "http://schemas.openxmlformats.org/package/2006/content-types";
    private const string XmlNs = "http://www.w3.org/XML/1998/namespace";

    public static void Write(string filePath, string sheetName, IReadOnlyList<object?[]> rows)
    {
        using var file = File.Create(filePath);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);
        WriteEntry(archive, "[Content_Types].xml", writer =>
        {
            writer.WriteStartElement("Types", ContentTypeNs);
            writer.WriteStartElement("Default", ContentTypeNs);
            writer.WriteAttributeString("Extension", "rels");
            writer.WriteAttributeString("ContentType", "application/vnd.openxmlformats-package.relationships+xml");
            writer.WriteEndElement();
            writer.WriteStartElement("Default", ContentTypeNs);
            writer.WriteAttributeString("Extension", "xml");
            writer.WriteAttributeString("ContentType", "application/xml");
            writer.WriteEndElement();
            writer.WriteStartElement("Override", ContentTypeNs);
            writer.WriteAttributeString("PartName", "/xl/workbook.xml");
            writer.WriteAttributeString("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
            writer.WriteEndElement();
            writer.WriteStartElement("Override", ContentTypeNs);
            writer.WriteAttributeString("PartName", "/xl/worksheets/sheet1.xml");
            writer.WriteAttributeString("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
            writer.WriteEndElement();
            writer.WriteEndElement();
        });
        WriteEntry(archive, "_rels/.rels", writer =>
        {
            writer.WriteStartElement("Relationships", PackageRelNs);
            writer.WriteStartElement("Relationship", PackageRelNs);
            writer.WriteAttributeString("Id", "rId1");
            writer.WriteAttributeString("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument");
            writer.WriteAttributeString("Target", "xl/workbook.xml");
            writer.WriteEndElement();
            writer.WriteEndElement();
        });
        WriteEntry(archive, "xl/workbook.xml", writer =>
        {
            writer.WriteStartElement("workbook", SpreadsheetNs);
            writer.WriteAttributeString("xmlns", "r", null, OfficeRelNs);
            writer.WriteStartElement("sheets", SpreadsheetNs);
            writer.WriteStartElement("sheet", SpreadsheetNs);
            writer.WriteAttributeString("name", sheetName);
            writer.WriteAttributeString("sheetId", "1");
            writer.WriteAttributeString("r", "id", OfficeRelNs, "rId1");
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndElement();
        });
        WriteEntry(archive, "xl/_rels/workbook.xml.rels", writer =>
        {
            writer.WriteStartElement("Relationships", PackageRelNs);
            writer.WriteStartElement("Relationship", PackageRelNs);
            writer.WriteAttributeString("Id", "rId1");
            writer.WriteAttributeString("Type", OfficeRelNs + "/worksheet");
            writer.WriteAttributeString("Target", "worksheets/sheet1.xml");
            writer.WriteEndElement();
            writer.WriteEndElement();
        });
        WriteEntry(archive, "xl/worksheets/sheet1.xml", writer =>
        {
            writer.WriteStartElement("worksheet", SpreadsheetNs);
            writer.WriteStartElement("sheetData", SpreadsheetNs);
            for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                writer.WriteStartElement("row", SpreadsheetNs);
                writer.WriteAttributeString("r", (rowIndex + 1).ToString(CultureInfo.InvariantCulture));
                var row = rows[rowIndex];
                for (var columnIndex = 0; columnIndex < row.Length; columnIndex++)
                {
                    var value = row[columnIndex];
                    if (value is null) continue;
                    writer.WriteStartElement("c", SpreadsheetNs);
                    writer.WriteAttributeString("r", CellReference(columnIndex + 1, rowIndex + 1));
                    if (IsNumeric(value))
                    {
                        writer.WriteStartElement("v", SpreadsheetNs);
                        writer.WriteString(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "0");
                        writer.WriteEndElement();
                    }
                    else
                    {
                        writer.WriteAttributeString("t", "inlineStr");
                        writer.WriteStartElement("is", SpreadsheetNs);
                        writer.WriteStartElement("t", SpreadsheetNs);
                        writer.WriteAttributeString("xml", "space", XmlNs, "preserve");
                        writer.WriteString(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
                        writer.WriteEndElement();
                        writer.WriteEndElement();
                    }
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
            writer.WriteEndElement();
        });
    }

    private static void WriteEntry(ZipArchive archive, string path, Action<XmlWriter> writeContent)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = true,
            CloseOutput = false
        });
        writer.WriteStartDocument();
        writeContent(writer);
        writer.WriteEndDocument();
    }

    private static string CellReference(int column, int row)
    {
        var letters = "";
        while (column > 0)
        {
            var remainder = (column - 1) % 26;
            letters = (char)('A' + remainder) + letters;
            column = (column - 1) / 26;
        }
        return letters + row.ToString(CultureInfo.InvariantCulture);
    }

    private static bool IsNumeric(object value) =>
        value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;
}
