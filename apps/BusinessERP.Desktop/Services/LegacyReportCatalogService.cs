using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace BusinessERP.Desktop.Services;

public sealed record LegacyReportTemplate(
    string FileName,
    string Category,
    string SerializerVersion,
    string PaperKind,
    int ControlCount,
    int BoundFieldCount,
    string BoundFields,
    string IntegrationStatus,
    string FullPath);

/// <summary>
/// Reads report layout metadata only. It never evaluates expressions or instantiates a report data source.
/// </summary>
public static class LegacyReportCatalogService
{
    private static readonly Regex FieldExpression =
        new(@"\[([A-Za-z_][\w.]*)\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<LegacyReportTemplate> Load(string directory)
    {
        if (!Directory.Exists(directory))
            return [];

        var reports = new List<LegacyReportTemplate>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.repx", SearchOption.TopDirectoryOnly)
                     .OrderBy(file => Path.GetFileName(file), StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var reader = XmlReader.Create(path, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = 10_000_000
                });
                var document = XDocument.Load(reader);
                var root = document.Root;
                if (root is null || root.Name.LocalName != "XtraReportsLayoutSerializer")
                    continue;

                var expressions = root.DescendantsAndSelf()
                    .SelectMany(element => element.Attributes()
                        .Where(attribute => attribute.Name.LocalName.Equals("Expression", StringComparison.OrdinalIgnoreCase))
                        .Select(attribute => attribute.Value));

                var fields = expressions
                    .SelectMany(expression => FieldExpression.Matches(expression).Cast<Match>()
                        .Select(match => match.Groups[1].Value))
                    .Where(name => !name.StartsWith("DataSource.", StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                reports.Add(new LegacyReportTemplate(
                    Path.GetFileName(path),
                    Categorize(Path.GetFileNameWithoutExtension(path)),
                    (string?)root.Attribute("SerializerVersion") ?? (string?)root.Attribute("Version") ?? "غير معروف",
                    (string?)root.Attribute("PaperKind") ?? "Default",
                    root.DescendantsAndSelf().Count(element => element.Attribute("ControlType") is not null),
                    fields.Length,
                    string.Join(", ", fields.Take(10)) + (fields.Length > 10 ? "، ..." : string.Empty),
                    "قالب محفوظ؛ ربط البيانات ومحرك العرض غير مكتمل",
                    path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or InvalidOperationException)
            {
                // A corrupt or unsupported layout must not stop the entire report workspace from loading.
            }
        }

        return reports;
    }

    private static string Categorize(string name)
    {
        if (Regex.IsMatch(name, @"POS|Barcode|daysalesPos|InvDetailsPos|InvSumtPos|PurchDetailsPos", RegexOptions.IgnoreCase))
            return "نقطة البيع والفواتير";
        if (Regex.IsMatch(name, @"Tax|Vat", RegexOptions.IgnoreCase))
            return "الضريبة";
        if (Regex.IsMatch(name, @"Cust|Client|Supplier|Account|Statement", RegexOptions.IgnoreCase))
            return "العملاء والحسابات";
        if (Regex.IsMatch(name, @"Purch|Purchase", RegexOptions.IgnoreCase))
            return "المشتريات";
        if (Regex.IsMatch(name, @"Inv|Item|Inventory|Serial|Production|Pricing", RegexOptions.IgnoreCase))
            return "المخزون والأصناف";
        if (Regex.IsMatch(name, @"Sales|Sale|NetSales|daysales", RegexOptions.IgnoreCase))
            return "المبيعات";
        if (Regex.IsMatch(name, @"Safe|Khzna|Bond|Payment|Receipt|Close|Shift|Entry|Daily|SummaryD", RegexOptions.IgnoreCase))
            return "الخزائن والسندات";
        if (Regex.IsMatch(name, @"Income|Mezan|CostCenter|Balance|Summary", RegexOptions.IgnoreCase))
            return "التقارير المالية";
        return "عام";
    }
}
