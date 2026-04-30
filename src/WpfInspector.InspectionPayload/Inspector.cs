using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.IO;
using WpfInspector.Core.Models;

namespace WpfInspector.InspectionPayload;

public static class Inspector
{
    private const int MaxDepth = 20;
    private const int MaxChildren = 40;

    public static string CollectJson(int processId)
    {
        var result = CollectResult(processId);
        return JsonSerializer.Serialize(result);
    }

    internal static WpfInspectionResult CollectResult(int processId)
    {
        var windows = new List<WpfWindowInfo>();
        var runtimeAssemblies = new List<WpfRuntimeAssemblyInfo>();
        string wpfVersion = string.Empty;
        string dispatcher = string.Empty;

        var app = Application.Current;
        if (app is null)
            throw new InvalidOperationException("Application.Current が null です。対象プロセスで WPF Application が起動していません。");

        app.Dispatcher.Invoke(() =>
        {
            var collector = new RuntimeAssemblyCollector(AppDomain.CurrentDomain.BaseDirectory);
            dispatcher = app.Dispatcher.Thread.ThreadState.ToString();

            foreach (Window w in app.Windows)
            {
                collector.Record(w.GetType(), "Window");
                windows.Add(new WpfWindowInfo(
                    Title: w.Title,
                    Width: w.ActualWidth,
                    Height: w.ActualHeight,
                    WindowState: w.WindowState.ToString(),
                    RootElement: CollectElement(w, depth: 0, collector)));
            }

            var pfAsm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "PresentationFramework");
            wpfVersion = pfAsm?.GetName().Version?.ToString() ?? "不明";
            runtimeAssemblies = collector.ToList();
        }, DispatcherPriority.Normal);

        return new WpfInspectionResult(
            ProcessId: processId,
            WpfVersion: wpfVersion,
            DispatcherState: dispatcher,
            RuntimeAssemblies: runtimeAssemblies,
            Windows: windows);
    }

    private static WpfElementInfo CollectElement(DependencyObject obj, int depth, RuntimeAssemblyCollector collector)
    {
        var type = obj.GetType();
        collector.Record(type, "Visual");

        var typeName = type.Name;
        var name = obj is FrameworkElement fe ? (fe.Name ?? string.Empty) : string.Empty;
        var isVisible = obj is UIElement ui && ui.IsVisible;
        var isEnabled = obj is UIElement uiEnabled && uiEnabled.IsEnabled;
        var width = obj is FrameworkElement fw ? fw.ActualWidth : 0d;
        var height = obj is FrameworkElement fh ? fh.ActualHeight : 0d;

        var automationId = obj is FrameworkElement
            ? (AutomationProperties.GetAutomationId(obj) is { Length: > 0 } aid ? aid : null)
            : null;

        string? text = null;
        if (obj is TextBlock tb) text = tb.Text;
        else if (obj is TextBox tbx) text = tbx.Text;
        else if (obj is ContentControl cc && cc.Content is string s) text = s;

        Type? dataContextType = null;
        if (obj is FrameworkElement frameworkElement && frameworkElement.DataContext is not null)
        {
            dataContextType = frameworkElement.DataContext.GetType();
            collector.Record(dataContextType, "DataContext");
        }

        var children = new List<WpfElementInfo>();
        if (depth < MaxDepth)
        {
            int count = VisualTreeHelper.GetChildrenCount(obj);
            for (int i = 0; i < Math.Min(count, MaxChildren); i++)
            {
                var child = VisualTreeHelper.GetChild(obj, i);
                if (ShouldSkip(child)) continue;
                children.Add(CollectElement(child, depth + 1, collector));
            }
        }

        return new WpfElementInfo(
            TypeName: typeName,
            Name: name,
            AutomationId: automationId,
            Text: text,
            DataContextTypeName: dataContextType?.Name,
            IsVisible: isVisible,
            IsEnabled: isEnabled,
            Width: width,
            Height: height,
            Children: children);
    }

    private static bool ShouldSkip(DependencyObject obj)
    {
        if (obj is not UIElement ui) return false;
        if (ui.IsVisible) return false;
        if (obj is not FrameworkElement fe) return false;
        return fe.ActualWidth == 0 && fe.ActualHeight == 0;
    }

    private sealed class RuntimeAssemblyCollector
    {
        private readonly string _appBaseDirectory;
        private readonly Dictionary<string, WpfRuntimeAssemblyInfo> _assemblies = new(StringComparer.OrdinalIgnoreCase);

        public RuntimeAssemblyCollector(string appBaseDirectory)
        {
            _appBaseDirectory = appBaseDirectory;
        }

        public void Record(Type type, string discoverySource)
        {
            var assembly = type.Assembly;
            var assemblyName = assembly.GetName();
            var location = GetAssemblyLocation(assembly);
            var key = !string.IsNullOrWhiteSpace(location)
                ? location
                : assemblyName.FullName ?? assemblyName.Name ?? type.FullName ?? type.Name;

            if (_assemblies.TryGetValue(key, out var existing))
            {
                if (GetDiscoverySourcePriority(discoverySource) < GetDiscoverySourcePriority(existing.DiscoverySource))
                {
                    _assemblies[key] = existing with
                    {
                        RepresentativeTypeName = type.FullName ?? type.Name,
                        DiscoverySource = discoverySource
                    };
                }

                return;
            }

            _assemblies[key] = new WpfRuntimeAssemblyInfo(
                AssemblyName: assemblyName.Name ?? "<unknown>",
                AssemblyPath: location,
                AssemblyVersion: assemblyName.Version?.ToString(),
                RepresentativeTypeName: type.FullName ?? type.Name,
                DiscoverySource: discoverySource,
                IsDynamic: assembly.IsDynamic,
                IsInAppDirectory: IsInAppDirectory(location));
        }

        public List<WpfRuntimeAssemblyInfo> ToList() =>
            _assemblies.Values
                .OrderByDescending(assembly => assembly.IsInAppDirectory)
                .ThenBy(assembly => GetDiscoverySourcePriority(assembly.DiscoverySource))
                .ThenBy(assembly => assembly.AssemblyName, StringComparer.OrdinalIgnoreCase)
                .ToList();

        private bool IsInAppDirectory(string? assemblyPath)
        {
            if (string.IsNullOrWhiteSpace(assemblyPath))
            {
                return false;
            }

            var appBasePath = Path.GetFullPath(_appBaseDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var fullAssemblyPath = Path.GetFullPath(assemblyPath);
            return fullAssemblyPath.StartsWith(appBasePath, StringComparison.OrdinalIgnoreCase);
        }

        private static string? GetAssemblyLocation(System.Reflection.Assembly assembly)
        {
            if (assembly.IsDynamic)
            {
                return null;
            }

            try
            {
                return string.IsNullOrWhiteSpace(assembly.Location) ? null : assembly.Location;
            }
            catch
            {
                return null;
            }
        }

        private static int GetDiscoverySourcePriority(string discoverySource) => discoverySource switch
        {
            "Window" => 0,
            "Visual" => 1,
            "DataContext" => 2,
            _ => 3
        };
    }
}
