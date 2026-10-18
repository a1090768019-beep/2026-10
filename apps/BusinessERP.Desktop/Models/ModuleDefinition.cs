namespace BusinessERP.Desktop.Models;

public sealed record ModuleDefinition(
    string Key,
    string Name,
    string Description,
    string Code,
    string Status);
