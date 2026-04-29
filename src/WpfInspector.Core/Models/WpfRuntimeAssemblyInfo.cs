namespace WpfInspector.Core.Models;

public sealed record WpfRuntimeAssemblyInfo(
    string AssemblyName,
    string? AssemblyPath,
    string? AssemblyVersion,
    string RepresentativeTypeName,
    string DiscoverySource,
    bool IsDynamic,
    bool IsInAppDirectory);
