using BusinessERP.Domain.Accounting;
using Xunit;

namespace BusinessERP.Domain.Tests;

public sealed class JournalEntryValidatorTests
{
    private static JournalEntryDraft Draft(params JournalEntryLineDraft[] lines) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "SAR", lines);

    [Fact]
    public void Balanced_entry_is_valid()
    {
        var draft = Draft(
            new(Guid.NewGuid(), 125.50m, 0m),
            new(Guid.NewGuid(), 0m, 125.50m));
        Assert.Empty(JournalEntryValidator.Validate(draft));
    }

    [Fact]
    public void Unbalanced_entry_is_rejected()
    {
        var draft = Draft(
            new(Guid.NewGuid(), 125.50m, 0m),
            new(Guid.NewGuid(), 0m, 125.49m));
        Assert.Contains(JournalEntryValidator.Validate(draft), error => error.Contains("equal", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void One_line_entry_is_rejected()
    {
        var errors = JournalEntryValidator.Validate(Draft(new JournalEntryLineDraft(Guid.NewGuid(), 10m, 0m)));
        Assert.Contains(errors, error => error.Contains("at least two", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Negative_amount_is_rejected()
    {
        var errors = JournalEntryValidator.Validate(Draft(
            new(Guid.NewGuid(), -1m, 0m), new(Guid.NewGuid(), 0m, -1m)));
        Assert.Contains(errors, error => error.Contains("negative", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_line_cannot_have_both_debit_and_credit()
    {
        var errors = JournalEntryValidator.Validate(Draft(
            new(Guid.NewGuid(), 10m, 10m), new(Guid.NewGuid(), 0m, 20m)));
        Assert.Contains(errors, error => error.Contains("both debit and credit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Zero_value_line_is_rejected()
    {
        var errors = JournalEntryValidator.Validate(Draft(
            new(Guid.NewGuid(), 0m, 0m), new(Guid.NewGuid(), 0m, 0m)));
        Assert.Contains(errors, error => error.Contains("must be positive", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decimal_total_overflow_is_rejected()
    {
        var errors = JournalEntryValidator.Validate(Draft(
            new(Guid.NewGuid(), decimal.MaxValue, 0m),
            new(Guid.NewGuid(), decimal.MaxValue, 0m),
            new(Guid.NewGuid(), 0m, decimal.MaxValue),
            new(Guid.NewGuid(), 0m, decimal.MaxValue)));
        Assert.Contains(errors, error => error.Contains("supported decimal range", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Missing_account_is_rejected()
    {
        var errors = JournalEntryValidator.Validate(Draft(
            new(Guid.Empty, 10m, 0m), new(Guid.NewGuid(), 0m, 10m)));
        Assert.Contains(errors, error => error.Contains("AccountId", StringComparison.Ordinal));
    }
}
