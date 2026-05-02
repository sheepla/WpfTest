namespace WpfInspector.Core.Models;

public sealed record WpfCommandInfo(
    string? CommandTypeName,
    string? CommandName,
    string? OwnerTypeName,
    string? CommandParameterTypeName,
    string? CommandParameterSummary,
    string? CommandTargetStableId,
    string? CommandTargetTypeName,
    bool? CanExecute,
    List<WpfElementReference> CommandBindingOwners,
    List<WpfCodeReferenceInfo> CodeReferences);
