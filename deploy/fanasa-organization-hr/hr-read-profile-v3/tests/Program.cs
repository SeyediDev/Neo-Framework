using Fanasa.HrReadCandidate;

try
{
    SettingsTests.Run(); WireTests.Run(); await WireTests.RunAsync(); await CoordinatorTests.RunAsync(); await BudgetTests.RunAsync(); await ProfileTests.RunAsync(); await FabricTests.RunAsync();
    Console.WriteLine($"PASS: {Check.Count} local assertions. No APISIX, TLS or deployment acceptance.");
}
catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
internal static class Check
{
    public static int Count { get; private set; }
    public static void True(bool value, string message) { if (!value) throw new InvalidOperationException(message); Count++; }
    public static void Throws<T>(Action action, string message) where T : Exception
    { try { action(); } catch (T) { Count++; return; } throw new InvalidOperationException(message); }
    public static async Task ThrowsAsync<T>(Func<Task> action, string message) where T : Exception
    { try { await action(); } catch (T) { Count++; return; } throw new InvalidOperationException(message); }
}
