using System.Globalization;
using System.Windows.Controls;
using System.Text.RegularExpressions;

namespace WpfInspector.App.Validation;

public sealed class TokenFormatValidationRule : ValidationRule
{
    private static readonly Regex TokenPattern = new("^[A-Z0-9-]{4,}$", RegexOptions.Compiled);

    public override ValidationResult Validate(object value, CultureInfo cultureInfo)
    {
        var text = value as string ?? string.Empty;
        return TokenPattern.IsMatch(text)
            ? ValidationResult.ValidResult
            : new ValidationResult(false, "大文字英数字と '-' を 4 文字以上で入力してください。");
    }
}
