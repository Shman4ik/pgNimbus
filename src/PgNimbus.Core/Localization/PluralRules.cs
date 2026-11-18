namespace PgNimbus.Core.Localization;

/// <summary>
/// CLDR cardinal plural categories for whole numbers
/// (<see href="https://www.unicode.org/cldr/charts/latest/supplemental/language_plural_rules.html"/>):
/// which of <c>zero one two few many other</c> a count takes in a language. A
/// string table holds <c>Key_one</c>, <c>Key_few</c>, … as its language needs; a
/// form it lacks reads as <c>Key_other</c>. A language not listed here is treated
/// like English (one for 1, other for the rest); add its rule with its first
/// translation.
/// </summary>
public static class PluralRules
{
    public static string Category(string language, long count)
    {
        var n = Math.Abs(count);
        var dash = language.IndexOf('-');
        var lang = (dash < 0 ? language : language[..dash]).ToLowerInvariant();

        switch (lang)
        {
            // No plural forms at all.
            case "zh" or "ja" or "ko" or "vi" or "th" or "id" or "ms" or "lo" or "my":
                return "other";

            // 0 and 1 are singular.
            case "fr" or "hi" or "fa" or "bn" or "am" or "kab":
                return n is 0 or 1 ? "one" : "other";

            case "ru" or "uk" or "be":
            {
                var mod10 = n % 10;
                var mod100 = n % 100;
                if (mod10 == 1 && mod100 != 11)
                {
                    return "one";
                }

                return mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14 ? "few" : "many";
            }

            case "pl":
            {
                if (n == 1)
                {
                    return "one";
                }

                var mod10 = n % 10;
                var mod100 = n % 100;
                return mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14 ? "few" : "many";
            }

            case "cs" or "sk":
                return n == 1 ? "one" : n is >= 2 and <= 4 ? "few" : "other";

            default:
                return n == 1 ? "one" : "other";
        }
    }
}
