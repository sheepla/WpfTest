using System.Collections;
using System.Globalization;
using System.Reflection;
using WpfInspector.App.ViewModels;

namespace WpfInspector.App.Services;

internal static class InspectionObjectFormatter
{
    public static IReadOnlyList<InspectionPropertyItem> Describe(object? instance, params string[] excludedPropertyNames)
    {
        if (instance is null)
        {
            return Array.Empty<InspectionPropertyItem>();
        }

        var excluded = new HashSet<string>(excludedPropertyNames, StringComparer.Ordinal);
        var properties = instance
            .GetType()
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.CanRead
                && property.GetIndexParameters().Length == 0
                && !excluded.Contains(property.Name))
            .OrderBy(property => GetPreferredOrder(instance.GetType(), property.Name))
            .ThenBy(property => property.Name, StringComparer.Ordinal);

        var items = new List<InspectionPropertyItem>();
        foreach (var property in properties)
        {
            items.Add(new InspectionPropertyItem(
                property.Name,
                FormatValue(ReadPropertyValue(property, instance))));
        }

        return items;
    }

    private static object? ReadPropertyValue(PropertyInfo property, object instance)
    {
        try
        {
            return property.GetValue(instance);
        }
        catch (Exception exception)
        {
            return $"<error: {exception.Message}>";
        }
    }

    private static int GetPreferredOrder(Type type, string propertyName)
    {
        string[] preferredNames = type.Name switch
        {
            "WpfInspectionResult" => ["ProcessId", "WpfVersion", "DispatcherState", "RuntimeAssemblies", "Windows"],
            "WpfWindowInfo" => ["Title", "WindowState", "Width", "Height", "RootElement"],
            "WpfElementInfo" => ["TypeName", "Name", "AutomationId", "Text", "DataContextTypeName", "IsVisible", "IsEnabled", "Width", "Height", "Children"],
            _ => []
        };

        for (var index = 0; index < preferredNames.Length; index++)
        {
            if (string.Equals(preferredNames[index], propertyName, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return 1000;
    }

    private static string FormatValue(object? value)
    {
        return value switch
        {
            null => "(null)",
            string text => string.IsNullOrWhiteSpace(text) ? "(empty)" : text,
            bool boolean => boolean ? "true" : "false",
            DateTime dateTime => dateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.CurrentCulture),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.CurrentCulture),
            TimeSpan timeSpan => timeSpan.ToString(),
            IEnumerable enumerable when value is not string => FormatEnumerable(enumerable),
            _ => IsSimpleValue(value.GetType())
                ? Convert.ToString(value, CultureInfo.CurrentCulture) ?? value.ToString() ?? string.Empty
                : SummarizeComplexObject(value)
        };
    }

    private static string FormatEnumerable(IEnumerable enumerable)
    {
        var count = 0;
        var preview = new List<string>();

        foreach (var item in enumerable)
        {
            count++;
            if (preview.Count < 4)
            {
                preview.Add(item is null ? "(null)" : SummarizeComplexObject(item));
            }
        }

        if (count == 0)
        {
            return "0 item(s)";
        }

        return count > preview.Count
            ? $"{count} item(s): {string.Join(", ", preview)} ..."
            : $"{count} item(s): {string.Join(", ", preview)}";
    }

    private static string SummarizeComplexObject(object value)
    {
        if (IsSimpleValue(value.GetType()))
        {
            return Convert.ToString(value, CultureInfo.CurrentCulture) ?? value.ToString() ?? string.Empty;
        }

        var interestingProperties = new[] { "Name", "Title", "TypeName", "Text", "AssemblyName", "RepresentativeTypeName", "ProcessName", "WindowState" };
        var parts = new List<string>();

        foreach (var propertyName in interestingProperties)
        {
            var property = value.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            if (property is null || property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            var propertyValue = ReadPropertyValue(property, value);
            if (propertyValue is string text && !string.IsNullOrWhiteSpace(text))
            {
                parts.Add(text);
            }
        }

        return parts.Count == 0
            ? value.GetType().Name
            : $"{value.GetType().Name}: {string.Join(" | ", parts.Distinct(StringComparer.Ordinal))}";
    }

    private static bool IsSimpleValue(Type type)
    {
        var underlyingType = Nullable.GetUnderlyingType(type) ?? type;
        return underlyingType.IsPrimitive
            || underlyingType.IsEnum
            || underlyingType == typeof(decimal)
            || underlyingType == typeof(double)
            || underlyingType == typeof(float)
            || underlyingType == typeof(Guid);
    }
}
