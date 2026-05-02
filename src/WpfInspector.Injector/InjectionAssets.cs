namespace WpfInspector.Injector;

public static class InjectionAssets
{
    public const string CompatibleManagedPayloadTargetFramework = "net8.0-windows";
    public const string CompatibleManagedPayloadDirectoryName = "InjectionAssets";

    public static readonly string[] ManagedPayloadFiles =
    [
        "WpfInspector.TestHelper.dll",
        "WpfInspector.TestHelper.deps.json",
        "WpfInspector.TestHelper.runtimeconfig.json",
        "WpfInspector.InspectionPayload.dll",
        "WpfInspector.InspectionPayload.deps.json",
        "WpfInspector.InspectionPayload.runtimeconfig.json",
        "WpfInspector.Core.dll"
    ];

    public static string ResolveManagedPayloadBaseDirectory(string baseDirectory)
    {
        var compatibleAssetsDirectory = Path.Combine(
            baseDirectory,
            CompatibleManagedPayloadDirectoryName,
            CompatibleManagedPayloadTargetFramework);

        return Directory.Exists(compatibleAssetsDirectory)
            ? compatibleAssetsDirectory
            : baseDirectory;
    }

    public static string FindNativeBootstrap(string baseDirectory)
    {
        var path = Path.Combine(baseDirectory, "WpfInspector.NativeBootstrap.dll");
        if (File.Exists(path)) return path;

        var repoRoot = FindRepoRoot(baseDirectory);
        if (repoRoot is not null)
        {
            foreach (var config in new[] { "Debug", "Release" })
            {
                var candidate = Path.Combine(
                    repoRoot, "src", "WpfInspector.NativeBootstrap", "x64", config,
                    "WpfInspector.NativeBootstrap.dll");
                if (File.Exists(candidate)) return candidate;
            }
        }

        throw new FileNotFoundException(
            "WpfInspector.NativeBootstrap.dll が見つかりません。msbuild で NativeBootstrap プロジェクトをビルドしてください。");
    }

    private static string? FindRepoRoot(string start)
    {
        var dir = start;
        for (int i = 0; i < 8; i++)
        {
            if (File.Exists(Path.Combine(dir, "WpfInspector.sln"))) return dir;
            var parent = Path.GetDirectoryName(dir);
            if (parent is null) break;
            dir = parent;
        }
        return null;
    }
}
