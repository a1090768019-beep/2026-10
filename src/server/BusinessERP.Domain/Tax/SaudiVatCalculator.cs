namespace BusinessERP.Domain.Tax;

/// <summary>
/// Internal VAT arithmetic for preview purposes. This does not issue a tax invoice or implement
/// ZATCA XML, QR, hashing, signing, clearance or reporting. The preview calculation policy rounds
/// gross line amount and VAT per line to two decimals using AwayFromZero rounding.
/// Classification of each supply must be reviewed against the applicable VAT rules.
/// </summary>
public static class SaudiVatCalculator
{
    public const decimal StandardRatePercent = 15m;
    public const int MoneyScale = 2;
    public const int InputPrecision = 4;
    public const int MaximumLines = 500;
    public const decimal MaximumMoney = 999_999_999_999_999.99m;

    public static SaudiVatCalculation Calculate(IReadOnlyCollection<SalesVatLineInput>? lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count is < 1 or > MaximumLines)
            throw new ArgumentOutOfRangeException(nameof(lines),
                $"A VAT preview must contain between 1 and {MaximumLines} lines.");

        var results = new List<SalesVatLineResult>(lines.Count);
        decimal totalGross = 0m, totalDiscount = 0m, totalNet = 0m, totalVat = 0m, totalAmount = 0m;
        var index = 0;

        foreach (var input in lines)
        {
            index++;
            if (input is null)
                throw new ArgumentException($"Line {index} is required.", nameof(lines));
            ValidateLine(input, index);

            decimal rawGross;
            try { rawGross = checked(input.Quantity * input.UnitPrice); }
            catch (OverflowException)
            {
                throw new ArgumentOutOfRangeException(nameof(lines),
                    $"Line {index} gross amount exceeds the supported range.");
            }

            if (rawGross > MaximumMoney)
                throw new ArgumentOutOfRangeException(nameof(lines),
                    $"Line {index} gross amount exceeds the supported range.");

            var gross = RoundMoney(rawGross);
            var discount = input.DiscountAmount;
            if (discount > gross)
                throw new ArgumentException(
                    $"Line {index}: discount cannot exceed the rounded gross amount.", nameof(lines));

            var net = RoundMoney(gross - discount);
            var rate = input.Treatment == SalesVatTreatment.StandardRated ? StandardRatePercent : 0m;
            var vat = RoundMoney(checked(net * rate / 100m));
            var lineTotal = checked(net + vat);
            if (lineTotal > MaximumMoney)
                throw new ArgumentOutOfRangeException(nameof(lines),
                    $"Line {index} total exceeds the supported range.");

            try
            {
                totalGross = checked(totalGross + gross);
                totalDiscount = checked(totalDiscount + discount);
                totalNet = checked(totalNet + net);
                totalVat = checked(totalVat + vat);
                totalAmount = checked(totalAmount + lineTotal);
            }
            catch (OverflowException)
            {
                throw new ArgumentOutOfRangeException(nameof(lines), "Invoice totals exceed the supported range.");
            }

            results.Add(new SalesVatLineResult(input.Description.Trim(), input.Treatment, gross,
                discount, net, rate, vat, lineTotal,
                string.IsNullOrWhiteSpace(input.TreatmentReason) ? null : input.TreatmentReason.Trim()));
        }

        return new SaudiVatCalculation(results, RoundMoney(totalGross), RoundMoney(totalDiscount),
            RoundMoney(totalNet), RoundMoney(totalVat), RoundMoney(totalAmount));
    }

    private static void ValidateLine(SalesVatLineInput input, int lineNumber)
    {
        if (string.IsNullOrWhiteSpace(input.Description) || input.Description.Trim().Length > 500)
            throw new ArgumentException($"Line {lineNumber}: description must contain 1 to 500 characters.", nameof(input));
        if (input.Quantity <= 0m || !HasPrecision(input.Quantity, InputPrecision) || input.Quantity > MaximumMoney)
            throw new ArgumentException($"Line {lineNumber}: quantity must be positive and have at most {InputPrecision} decimal places.", nameof(input));
        if (input.UnitPrice < 0m || !HasPrecision(input.UnitPrice, InputPrecision) || input.UnitPrice > MaximumMoney)
            throw new ArgumentException($"Line {lineNumber}: unit price must be non-negative and have at most {InputPrecision} decimal places.", nameof(input));
        if (input.DiscountAmount < 0m || !HasPrecision(input.DiscountAmount, MoneyScale) || input.DiscountAmount > MaximumMoney)
            throw new ArgumentException($"Line {lineNumber}: discount must be non-negative and have at most {MoneyScale} decimal places.", nameof(input));
        if (!Enum.IsDefined(input.Treatment))
            throw new ArgumentException($"Line {lineNumber}: VAT treatment is not supported.", nameof(input));
        if (input.Treatment != SalesVatTreatment.StandardRated && string.IsNullOrWhiteSpace(input.TreatmentReason))
            throw new ArgumentException($"Line {lineNumber}: a reason is required for non-standard VAT treatment.", nameof(input));
        if (input.TreatmentReason?.Length > 500)
            throw new ArgumentException($"Line {lineNumber}: VAT treatment reason must not exceed 500 characters.", nameof(input));
    }

    private static bool HasPrecision(decimal value, int scale) =>
        decimal.Round(value, scale, MidpointRounding.ToEven) == value;

    private static decimal RoundMoney(decimal value) =>
        decimal.Round(value, MoneyScale, MidpointRounding.AwayFromZero);
}

public enum SalesVatTreatment
{
    StandardRated = 1,
    ZeroRated = 2,
    Exempt = 3,
    OutsideScope = 4
}

public sealed record SalesVatLineInput(
    string Description, decimal Quantity, decimal UnitPrice, decimal DiscountAmount,
    SalesVatTreatment Treatment, string? TreatmentReason = null);

public sealed record SalesVatLineResult(
    string Description, SalesVatTreatment Treatment, decimal GrossAmount, decimal DiscountAmount,
    decimal NetAmount, decimal VatRatePercent, decimal VatAmount, decimal TotalAmount, string? TreatmentReason);

public sealed record SaudiVatCalculation(
    IReadOnlyList<SalesVatLineResult> Lines, decimal GrossAmount, decimal DiscountAmount,
    decimal NetAmount, decimal VatAmount, decimal TotalAmount);
