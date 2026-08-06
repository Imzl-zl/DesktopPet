using System.Text.RegularExpressions;

namespace DesktopPet.Infra.Diagnostics;

public static partial class SecretRedactor
{
    public const string Replacement = "[REDACTED]";

    public static string Redact(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var redacted = AuthorizationRegex().Replace(value, $"$1{Replacement}");
        redacted = NamedSecretRegex().Replace(redacted, match =>
            $"{match.Groups[1].Value}{match.Groups[2].Value}{Replacement}");
        redacted = QuerySecretRegex().Replace(redacted, match =>
            $"{match.Groups[1].Value}{Replacement}");
        redacted = OpenAiKeyRegex().Replace(redacted, Replacement);
        return HighEntropyTokenRegex().Replace(redacted, Replacement);
    }

    [GeneratedRegex(@"(?i)(""?Authorization""?\s*[:=]\s*""?)([^""\r\n,;}]+)")]
    private static partial Regex AuthorizationRegex();

    [GeneratedRegex(@"(?i)(""?(?:api[_-]?key|access[_-]?token|token|client[_-]?secret|credential|password|secret)""?)(\s*[:=]\s*""?)([^""\s,;&}]+)")]
    private static partial Regex NamedSecretRegex();

    [GeneratedRegex(@"(?i)([?&](?:api[_-]?key|access[_-]?token|token|client[_-]?secret|credential|password|secret)=)([^&#\s]+)")]
    private static partial Regex QuerySecretRegex();

    [GeneratedRegex(@"\bsk-[A-Za-z0-9_-]{8,}\b")]
    private static partial Regex OpenAiKeyRegex();

    [GeneratedRegex(@"\b(?:AKIA[0-9A-Z]{16}|[A-Za-z0-9_+/=-]{32,})\b")]
    private static partial Regex HighEntropyTokenRegex();
}

