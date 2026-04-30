using WpfInspector.LlmAgent;

var pid = 0;
var task = string.Empty;

for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--pid") int.TryParse(args[i + 1], out pid);
    if (args[i] == "--task") task = args[i + 1];
}

if (pid == 0 || string.IsNullOrEmpty(task))
{
    Console.Error.WriteLine("Usage: WpfInspector.LlmAgent --pid <pid> --task \"<task description>\"");
    return 1;
}

var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
    ?? throw new InvalidOperationException("ANTHROPIC_API_KEY 環境変数が設定されていません。");

await using var pipe = new WpfPipeClient(pid);
Console.WriteLine($"PID {pid} に TestHelper を注入中...");
await pipe.InjectAndConnectAsync();
Console.WriteLine("接続完了。エージェントを開始します。");

var agent = new LlmAgentRunner(pipe, apiKey);
await agent.RunAsync(task);
return 0;
