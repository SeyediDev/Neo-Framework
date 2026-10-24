using Fanasa.AccessManagement.Web.Governance;

static class PipDatetimeTests
{
    public static void Run(Action<bool, string> check)
    {
        var definition = new PipAttributeDefinition("employment.endsAt", "datetime");
        foreach (var invalid in new[] { "12:00Z", "12:00:00+00:00", "2026-10-23Z", "2026-10-23T12:00:00",
            "2026-10-23T12:00:00+03:30", "2026-10-23T12:00:00.Z", "2026-10-23T12:00:00.12345678Z", "2026-02-30T12:00:00Z" })
        {
            var rejected = false;
            try { PipSchema.ValidateValue(definition, invalid); }
            catch (InvalidDataException) { rejected = true; }
            check(rejected, "PIP datetime rejects incomplete, invalid or non-UTC value: " + invalid);
        }
        check(PipSchema.ValuesEqual(definition, "2026-10-23T12:00:00Z", "2026-10-23T12:00:00.0000000+00:00"),
            "PIP datetime equality compares explicit UTC instants across equivalent formats");
        check(!PipSchema.ValuesEqual(definition, "2026-10-23T12:00:00.0000001Z", "2026-10-23T12:00:00.0000002+00:00"),
            "PIP datetime equality preserves declared subsecond precision");
        var incompleteComparisonRejected = false;
        try { _ = PipSchema.ValuesEqual(definition, "12:00Z", "2026-10-23T12:00:00Z"); }
        catch (InvalidDataException) { incompleteComparisonRejected = true; }
        check(incompleteComparisonRejected, "PIP datetime comparisons never infer a missing date from the machine clock");
    }
}
