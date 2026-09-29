using System.Globalization;
using System.Text.Json;

namespace CoopBot.UI;

internal static class CoopBotText
{
    private static readonly IReadOnlyDictionary<string, string> English = LoadEnglish();

    internal static string Get(string source, string locale)
        => locale is "zhs" or "zht" ? source : English[source];

    internal static string Format(string source, string locale, params object[] arguments)
        => string.Format(CultureInfo.CurrentCulture, Get(source, locale), arguments);

    private static IReadOnlyDictionary<string, string> LoadEnglish()
    {
        using Stream stream = typeof(CoopBotText).Assembly.GetManifestResourceStream("CoopBot.UI.English.json")
            ?? throw new InvalidOperationException("Missing embedded CoopBot English UI catalog.");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidOperationException("Empty CoopBot English UI catalog.");
    }
}
