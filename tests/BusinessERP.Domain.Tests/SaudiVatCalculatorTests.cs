using BusinessERP.Domain.Tax;
using Xunit;

namespace BusinessERP.Domain.Tests;

public sealed class SaudiVatCalculatorTests
{
    [Fact]
    public void Standard_rated_supply_uses_fifteen_percent()
    {
        var result = SaudiVatCalculator.Calculate(new[]
        {
            new SalesVatLineInput("Consulting", 2m, 100m, 0m, SalesVatTreatment.StandardRated)
        });
        Assert.Equal(200.00m, result.GrossAmount);
        Assert.Equal(200.00m, result.NetAmount);
        Assert.Equal(15m, result.Lines[0].VatRatePercent);
        Assert.Equal(30.00m, result.VatAmount);
        Assert.Equal(230.00m, result.TotalAmount);
    }

    [Fact]
    public void Discount_reduces_net_amount_before_vat()
    {
        var result = SaudiVatCalculator.Calculate(new[]
        {
            new SalesVatLineInput("Service", 1m, 100m, 10m, SalesVatTreatment.StandardRated)
        });
        Assert.Equal(100.00m, result.GrossAmount);
        Assert.Equal(10.00m, result.DiscountAmount);
        Assert.Equal(90.00m, result.NetAmount);
        Assert.Equal(13.50m, result.VatAmount);
        Assert.Equal(103.50m, result.TotalAmount);
    }

    [Theory]
    [InlineData(SalesVatTreatment.ZeroRated, "Zero-rate reason")]
    [InlineData(SalesVatTreatment.Exempt, "Exemption reason")]
    [InlineData(SalesVatTreatment.OutsideScope, "Outside-scope reason")]
    public void Non_standard_treatments_apply_zero_vat_and_preserve_reason(
        SalesVatTreatment treatment, string reason)
    {
        var result = SaudiVatCalculator.Calculate(new[]
        {
            new SalesVatLineInput("Special supply", 1m, 100m, 0m, treatment, reason)
        });
        Assert.Equal(100.00m, result.NetAmount);
        Assert.Equal(0.00m, result.VatAmount);
        Assert.Equal(100.00m, result.TotalAmount);
        Assert.Equal(0m, result.Lines[0].VatRatePercent);
        Assert.Equal(reason, result.Lines[0].TreatmentReason);
    }

    [Fact]
    public void Amounts_are_rounded_per_line_to_two_decimals()
    {
        var result = SaudiVatCalculator.Calculate(new[]
        {
            new SalesVatLineInput("Fractional service", 2.5m, 12.3456m, 0m, SalesVatTreatment.StandardRated)
        });
        Assert.Equal(30.86m, result.GrossAmount);
        Assert.Equal(30.86m, result.NetAmount);
        Assert.Equal(4.63m, result.VatAmount);
        Assert.Equal(35.49m, result.TotalAmount);
    }

    [Fact]
    public void Empty_lines_are_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => SaudiVatCalculator.Calculate(Array.Empty<SalesVatLineInput>()));

    [Fact]
    public void Zero_quantity_is_rejected() =>
        Assert.Throws<ArgumentException>(() => SaudiVatCalculator.Calculate(new[]
        {
            new SalesVatLineInput("Invalid", 0m, 10m, 0m, SalesVatTreatment.StandardRated)
        }));

    [Fact]
    public void Non_standard_treatment_requires_a_reason() =>
        Assert.Throws<ArgumentException>(() => SaudiVatCalculator.Calculate(new[]
        {
            new SalesVatLineInput("No reason", 1m, 10m, 0m, SalesVatTreatment.Exempt)
        }));

    [Fact]
    public void Discount_cannot_exceed_rounded_gross_amount() =>
        Assert.Throws<ArgumentException>(() => SaudiVatCalculator.Calculate(new[]
        {
            new SalesVatLineInput("Over-discount", 1m, 10m, 10.01m, SalesVatTreatment.StandardRated)
        }));

    [Fact]
    public void Excessive_quantity_precision_is_rejected() =>
        Assert.Throws<ArgumentException>(() => SaudiVatCalculator.Calculate(new[]
        {
            new SalesVatLineInput("Precision", 1.00001m, 10m, 0m, SalesVatTreatment.StandardRated)
        }));

    [Fact]
    public void Excessive_line_count_is_rejected()
    {
        var lines = Enumerable.Range(1, SaudiVatCalculator.MaximumLines + 1)
            .Select(i => new SalesVatLineInput($"Line {i}", 1m, 1m, 0m, SalesVatTreatment.StandardRated)).ToArray();
        Assert.Throws<ArgumentOutOfRangeException>(() => SaudiVatCalculator.Calculate(lines));
    }
}
