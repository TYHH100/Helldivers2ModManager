namespace Helldivers2ModManager.Core.PatchKit;

public enum PatchParseSeverity
{
    Warning,
    Error,
}

public sealed record PatchParseIssue(PatchParseSeverity Severity, string Code, string Detail);
