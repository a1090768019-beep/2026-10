namespace BusinessERP.Domain.Accounting;

public sealed record JournalEntryLineDraft(
    Guid AccountId,
    decimal Debit,
    decimal Credit,
    string? Description = null);

public sealed record JournalEntryDraft(
    Guid TenantId,
    Guid CompanyId,
    string CurrencyCode,
    IReadOnlyCollection<JournalEntryLineDraft> Lines);

public static class JournalEntryValidator
{
    public static IReadOnlyList<string> Validate(JournalEntryDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var errors = new List<string>();

        if (draft.TenantId == Guid.Empty)
            errors.Add("TenantId is required.");
        if (draft.CompanyId == Guid.Empty)
            errors.Add("CompanyId is required.");
        if (string.IsNullOrWhiteSpace(draft.CurrencyCode) ||
            draft.CurrencyCode.Length != 3 ||
            draft.CurrencyCode.Any(ch => ch is < 'A' or > 'Z'))
            errors.Add("CurrencyCode must be a three-letter uppercase ISO-style code.");
        if (draft.Lines is null || draft.Lines.Count < 2)
        {
            errors.Add("A journal entry must contain at least two lines.");
            return errors;
        }

        decimal totalDebit = 0m;
        decimal totalCredit = 0m;
        var totalsOverflow = false;
        var lineNumber = 0;
        foreach (var line in draft.Lines)
        {
            lineNumber++;
            if (line.AccountId == Guid.Empty)
                errors.Add($"Line {lineNumber}: AccountId is required.");
            if (line.Debit < 0m || line.Credit < 0m)
                errors.Add($"Line {lineNumber}: debit and credit cannot be negative.");
            if (line.Debit > 0m && line.Credit > 0m)
                errors.Add($"Line {lineNumber}: a line cannot contain both debit and credit.");
            if (line.Debit == 0m && line.Credit == 0m)
                errors.Add($"Line {lineNumber}: either debit or credit must be positive.");

            if (!totalsOverflow)
            {
                try
                {
                    totalDebit = checked(totalDebit + line.Debit);
                    totalCredit = checked(totalCredit + line.Credit);
                }
                catch (OverflowException)
                {
                    totalsOverflow = true;
                }
            }
        }

        if (totalsOverflow)
            errors.Add("Journal entry totals exceed the supported decimal range.");
        else if (totalDebit != totalCredit)
            errors.Add("Total debit must equal total credit exactly.");

        return errors;
    }
}
