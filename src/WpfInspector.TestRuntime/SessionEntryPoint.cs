namespace WpfInspector.TestHelper;

public static class SessionEntryPoint
{
    public static async Task<int> RunAsync(string fullPipeName)
    {
        await UiCommandHandler.RunAsync(fullPipeName);
        return 0;
    }
}
