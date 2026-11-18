using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace PgNimbus.Strings.Generator;

/// <summary>
/// Reads the English base file (<c>Strings.resx</c>, passed as an AdditionalFile)
/// and writes <c>PgNimbus.Core.Localization.Strings</c>: one member per entry.
/// <list type="bullet">
///   <item>A plain entry is a property: <c>Strings.About_Title</c>, usable from XAML
///   as <c>{x:Static l:Strings.About_Title}</c>.</item>
///   <item>An entry with <c>{name}</c> placeholders is a method taking one argument
///   per placeholder, in the order they first appear.</item>
///   <item>A pair <c>Key_one</c> + <c>Key_other</c> is a plural: a method
///   <c>Key(long count, …)</c> that picks the form for the current language.</item>
/// </list>
/// The English text is compiled into each member, so English needs no file at run
/// time and a missing translation always falls back to it.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class StringsGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor BadKey = new(
        "PGS001", "Invalid string key", "String key '{0}' in Strings.resx is not a C# identifier",
        "Localization", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor DuplicateKey = new(
        "PGS002", "Duplicate string key", "String key '{0}' appears more than once in Strings.resx",
        "Localization", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor PluralWithoutOther = new(
        "PGS003", "Plural without _other", "Plural string '{0}' has a _one form but no '{0}_other'",
        "Localization", DiagnosticSeverity.Error, true);

    private static readonly DiagnosticDescriptor Unreadable = new(
        "PGS004", "Unreadable Strings.resx", "Strings.resx could not be read: {0}",
        "Localization", DiagnosticSeverity.Error, true);

    internal static readonly Regex Placeholder = new(@"\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.CultureInvariant);

    private static readonly string[] PluralSuffixes = ["_zero", "_one", "_two", "_few", "_many", "_other"];

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var baseFile = context.AdditionalTextsProvider
            .Where(static f => string.Equals(System.IO.Path.GetFileName(f.Path), "Strings.resx", StringComparison.OrdinalIgnoreCase))
            .Select(static (f, ct) => (f.Path, Text: f.GetText(ct)?.ToString()));

        context.RegisterSourceOutput(baseFile, static (spc, file) =>
        {
            if (file.Text is null)
            {
                return;
            }

            List<(string Key, string Value, string? Comment)> entries;
            try
            {
                entries = XDocument.Parse(file.Text).Root!
                    .Elements("data")
                    .Where(d => d.Attribute("type") is null)
                    .Select(d => ((string)d.Attribute("name")!, (string?)d.Element("value") ?? "", (string?)d.Element("comment")))
                    .ToList();
            }
            catch (Exception ex)
            {
                spc.ReportDiagnostic(Diagnostic.Create(Unreadable, Location.None, ex.Message));
                return;
            }

            spc.AddSource("Strings.g.cs", SourceText.From(Build(spc, entries), Encoding.UTF8));
        });
    }

    private static string Build(SourceProductionContext spc, List<(string Key, string Value, string? Comment)> entries)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var allKeys = new HashSet<string>(entries.Select(e => e.Key), StringComparer.Ordinal);
        var plain = new List<(string Key, string Value, string? Comment)>();
        var plurals = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var pluralComments = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var (key, value, comment) in entries)
        {
            if (!SyntaxFacts.IsValidIdentifier(key))
            {
                spc.ReportDiagnostic(Diagnostic.Create(BadKey, Location.None, key));
                continue;
            }

            if (!seen.Add(key))
            {
                spc.ReportDiagnostic(Diagnostic.Create(DuplicateKey, Location.None, key));
                continue;
            }

            // Leading underscore: metadata (the language's own name), not a UI string.
            if (key.StartsWith("_", StringComparison.Ordinal))
            {
                continue;
            }

            // A plural is a stem with both _one and _other in the base file; English
            // needs no other form, a translation adds the ones its language has.
            var suffix = PluralSuffixes.FirstOrDefault(s => key.EndsWith(s, StringComparison.Ordinal));
            var stem = suffix is null ? null : key.Substring(0, key.Length - suffix.Length);
            if (stem is not null && allKeys.Contains(stem + "_one") && allKeys.Contains(stem + "_other"))
            {
                if (!plurals.TryGetValue(stem, out var forms))
                {
                    plurals[stem] = forms = new Dictionary<string, string>(StringComparer.Ordinal);
                }

                forms[suffix!.Substring(1)] = value;
                pluralComments[stem] = pluralComments.TryGetValue(stem, out var c) ? c ?? comment : comment;
                continue;
            }

            plain.Add((key, value, comment));
        }

        var texts = plain.Where(e => Names(e.Value).Count == 0).ToList();

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/> from Localization/Strings.resx by PgNimbus.Strings.Generator.");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("namespace PgNimbus.Core.Localization;");
        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// The plain strings (no placeholders), by name: what XAML's <c>{l:Tr Key}</c> takes, so a");
        sb.AppendLine("/// misspelt key fails the XAML compile. Order follows Strings.resx and means nothing.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("public enum StringKey");
        sb.AppendLine("{");
        foreach (var (key, value, _) in texts)
        {
            sb.Append("    /// <summary>").Append(Escape(value).Replace("\n", " ")).AppendLine("</summary>");
            sb.Append("    ").Append(key).AppendLine(",");
        }

        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("/// <summary>Every user-facing string, in the language <see cref=\"Localizer\"/> has loaded.</summary>");
        sb.AppendLine("public static partial class Strings");
        sb.AppendLine("{");
        sb.AppendLine("    private static readonly string[] KeyNames =");
        sb.AppendLine("    [");
        foreach (var (key, _, _) in texts)
        {
            sb.Append("        \"").Append(key).AppendLine("\",");
        }

        sb.AppendLine("    ];");
        sb.AppendLine();
        sb.AppendLine("    private static readonly string[] EnglishTexts =");
        sb.AppendLine("    [");
        foreach (var (_, value, _) in texts)
        {
            sb.Append("        ").Append(Literal(value)).AppendLine(",");
        }

        sb.AppendLine("    ];");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>The string <paramref name=\"key\"/> names, in the loaded language.</summary>");
        sb.AppendLine("    public static string Get(StringKey key) => Localizer.Get(KeyNames[(int)key], EnglishTexts[(int)key]);");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>The key's name in the string files.</summary>");
        sb.AppendLine("    public static string NameOf(StringKey key) => KeyNames[(int)key];");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>The key's English text.</summary>");
        sb.AppendLine("    public static string EnglishOf(StringKey key) => EnglishTexts[(int)key];");
        sb.AppendLine();

        foreach (var (key, value, comment) in plain)
        {
            Doc(sb, value, comment);
            var names = Names(value);
            if (names.Count == 0)
            {
                sb.Append("    public static string ").Append(key).Append(" => Get(StringKey.").Append(key).AppendLine(");");
            }
            else
            {
                sb.Append("    public static string ").Append(key).Append('(')
                    .Append(string.Join(", ", names.Select(n => "object? " + Param(n)))).Append(") => Localizer.Format(Localizer.Get(\"")
                    .Append(key).Append("\", ").Append(Literal(value)).Append("), ").Append(NameArray(names)).Append(", ")
                    .Append(string.Join(", ", names.Select(Param))).AppendLine(");");
            }

            sb.AppendLine();
        }

        foreach (var pair in plurals)
        {
            var stem = pair.Key;
            var forms = pair.Value;
            if (!forms.TryGetValue("other", out var other))
            {
                spc.ReportDiagnostic(Diagnostic.Create(PluralWithoutOther, Location.None, stem));
                continue;
            }

            forms.TryGetValue("one", out var one);
            Doc(sb, other, pluralComments[stem]);
            var names = Names(string.Join(" ", forms.Values)).Where(n => n != "count").ToList();
            sb.Append("    public static string ").Append(stem).Append("(long count")
                .Append(string.Concat(names.Select(n => ", object? " + Param(n)))).Append(") => Localizer.Format(Localizer.Plural(\"")
                .Append(stem).Append("\", count, ").Append(Literal(one ?? other)).Append(", ").Append(Literal(other)).Append("), ")
                .Append(NameArray(["count", .. names])).Append(", count")
                .Append(string.Concat(names.Select(n => ", " + Param(n)))).AppendLine(");");
            sb.AppendLine();
        }

        sb.AppendLine("    /// <summary>Every key the base file defines, plural forms by their full key; for the tests.</summary>");
        sb.AppendLine("    internal static readonly string[] AllKeys =");
        sb.AppendLine("    [");
        foreach (var (key, _, _) in entries.Where(e => !e.Key.StartsWith("_", StringComparison.Ordinal)).Distinct())
        {
            sb.Append("        \"").Append(key).AppendLine("\",");
        }

        sb.AppendLine("    ];");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static List<string> Names(string value) =>
        Placeholder.Matches(value).Cast<Match>().Select(m => m.Groups[1].Value).Distinct().ToList();

    private static string Param(string name) =>
        SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;

    private static string NameArray(IEnumerable<string> names) =>
        "[" + string.Join(", ", names.Select(n => "\"" + n + "\"")) + "]";

    private static string Literal(string value) =>
        SymbolDisplay.FormatLiteral(value, quote: true);

    private static void Doc(StringBuilder sb, string value, string? comment)
    {
        sb.Append("    /// <summary>").Append(Escape(value).Replace("\n", " ")).AppendLine("</summary>");
        if (!string.IsNullOrWhiteSpace(comment))
        {
            sb.Append("    /// <remarks>").Append(Escape(comment!).Replace("\n", " ")).AppendLine("</remarks>");
        }
    }

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\r", "");
}
