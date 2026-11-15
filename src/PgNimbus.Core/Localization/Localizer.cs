using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Xml;

namespace PgNimbus.Core.Localization;

/// <summary>
/// The string table behind <see cref="Strings"/>. English is compiled into the
/// accessors, so with nothing loaded every string is English; <see cref="Load"/>
/// reads one translation (<c>Strings.&lt;tag&gt;.resx</c>, embedded in this
/// assembly) and every key it lacks still reads as English.
/// <para>
/// Deliberately not <c>ResourceManager</c>: that looks translations up in satellite
/// assemblies by <see cref="CultureInfo"/>, and the App runs with
/// <c>InvariantGlobalization</c>, where only the invariant culture exists, and under
/// NativeAOT a culture-named satellite crashed Avalonia's asset resolver at startup
/// (see the App csproj's <c>SatelliteResourceLanguages</c>). A translation here is an
/// ordinary manifest resource of this assembly, named by a plain string tag.
/// </para>
/// <para>
/// The language can change while the app runs: <see cref="Load"/> raises
/// <see cref="LanguageChanged"/> and pushes the new text to every
/// <see cref="Observe(StringKey)"/> subscriber, which is what XAML's
/// <c>{l:Tr Key}</c> binds to. Call it on the UI thread.
/// </para>
/// </summary>
public static class Localizer
{
    /// <summary>The base language. It has no file of its own.</summary>
    public const string English = "en";

    /// <summary>
    /// A made-up language for testing: every string English, accented and in
    /// brackets (<c>[Çöññéçţ]</c>), so text that stays plain on screen was never
    /// looked up here.
    /// </summary>
    public const string Pseudo = "qps-ploc";

    private const string ResourcePrefix = "PgNimbus.Strings.";
    private const string ResourceSuffix = ".resx";

    private static Dictionary<string, string>? _table;
    private static bool _pseudo;

    /// <summary>The loaded language's tag; <see cref="English"/> when none is.</summary>
    public static string Language { get; private set; } = English;

    private static readonly Lazy<IReadOnlyList<string>> Tags = new(FindTags);
    private static readonly Lazy<IReadOnlyList<LanguageInfo>> Languages = new(() =>
        [.. Tags.Value.Select(t => new LanguageInfo(t, t == English ? "English" : NativeNameOf(t) ?? t))]);

    /// <summary>
    /// The languages this build carries, English first, each named in itself. Reads
    /// every translation's first entry, so only Settings asks for it.
    /// </summary>
    public static IReadOnlyList<LanguageInfo> Available => Languages.Value;

    /// <summary>
    /// Switches to <paramref name="tag"/>: <see cref="English"/>, <see cref="Pseudo"/>,
    /// or a tag one of <see cref="Available"/> carries. Anything else leaves English.
    /// </summary>
    public static void Load(string tag)
    {
        LoadCore(tag);
        Publish();
    }

    /// <summary>
    /// Switches to a translation that is not built in: <paramref name="table"/> (as
    /// <see cref="Read"/> returns it), under <paramref name="tag"/>, whose plural rules
    /// it follows. For the tests, and for a translator trying a file in a released
    /// build (<c>PGNIMBUS_TRANSLATION_FILE</c>, read by the App at startup).
    /// </summary>
    public static void LoadTable(string tag, IReadOnlyDictionary<string, string> table)
    {
        _pseudo = false;
        _table = new Dictionary<string, string>(table, StringComparer.Ordinal);
        Language = tag;
        Publish();
    }

    /// <summary>
    /// Raised after <see cref="Load"/> switched the language. A static event holds its
    /// handlers strongly: for anything that can close (a window, a view model), use
    /// <see cref="WhenChanged{T}"/>, which does not keep its owner alive.
    /// </summary>
    public static event Action? LanguageChanged;

    /// <summary>
    /// Calls <paramref name="handler"/> with <paramref name="owner"/> after every change
    /// of language, for as long as <paramref name="owner"/> is alive. The handler must
    /// not capture the owner (make it <c>static</c>): it gets it as its argument.
    /// Typical use, in a view model whose properties compose text:
    /// <c>Localizer.WhenChanged(this, static vm => vm.OnPropertyChanged(nameof(Label)));</c>
    /// </summary>
    public static void WhenChanged<T>(T owner, Action<T> handler)
        where T : class
    {
        var handlers = Owners.GetOrCreateValue(owner);
        lock (handlers)
        {
            handlers.Add(o => handler((T)o));
        }
    }

    private static readonly ConditionalWeakTable<object, List<Action<object>>> Owners = new();

    /// <summary>
    /// The string <paramref name="key"/> names, now and after every change of
    /// language. Observers are held weakly: a binding lives as long as its control.
    /// </summary>
    public static IObservable<string> Observe(StringKey key) => new Live(() => Strings.Get(key));

    /// <summary>
    /// <paramref name="compute"/>'s result now and after every change of language,
    /// for text composed from several strings or with placeholders filled.
    /// </summary>
    public static IObservable<string> Observe(Func<string> compute) => new Live(compute);

    // Weak keys: a binding's observer lives as long as its control, and a closed
    // window must not be kept alive by the language list. Enumerable since .NET 8.
    private static readonly ConditionalWeakTable<IObserver<string>, Func<string>> Observers = new();

    private static void Publish()
    {
        foreach (var (observer, compute) in Observers.ToArray())
        {
            observer.OnNext(compute());
        }

        foreach (var (owner, handlers) in Owners.ToArray())
        {
            Action<object>[] snapshot;
            lock (handlers)
            {
                snapshot = [.. handlers];
            }

            foreach (var handler in snapshot)
            {
                handler(owner);
            }
        }

        LanguageChanged?.Invoke();
    }

    private sealed class Live(Func<string> compute) : IObservable<string>
    {
        public IDisposable Subscribe(IObserver<string> observer)
        {
            observer.OnNext(compute());
            Observers.AddOrUpdate(observer, compute);
            return new Unsubscribe(observer);
        }
    }

    private sealed class Unsubscribe(IObserver<string> observer) : IDisposable
    {
        public void Dispose() => Observers.Remove(observer);
    }

    private static void LoadCore(string tag)
    {
        _table = null;
        _pseudo = false;
        Language = English;

        if (string.Equals(tag, Pseudo, StringComparison.OrdinalIgnoreCase))
        {
            _pseudo = true;
            Language = Pseudo;
            return;
        }

        if (Tags.Value.FirstOrDefault(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)) is not { } language ||
            language == English)
        {
            return;
        }

        using var stream = typeof(Localizer).Assembly.GetManifestResourceStream(ResourcePrefix + language + ResourceSuffix);
        if (stream is null)
        {
            return;
        }

        _table = Read(stream);
        Language = language;
    }

    /// <summary>
    /// The language to use for a setting of <paramref name="setting"/> ("auto" or a
    /// tag) on a system that prefers <paramref name="systemLanguages"/>, best first.
    /// </summary>
    public static string Resolve(string? setting, IEnumerable<string> systemLanguages) =>
        Resolve(setting, systemLanguages, Tags.Value);

    /// <summary><see cref="Resolve(string?, IEnumerable{string})"/> against a given set of carried tags.</summary>
    public static string Resolve(string? setting, IEnumerable<string> systemLanguages, IReadOnlyList<string> carried)
    {
        if (!string.IsNullOrWhiteSpace(setting) && !string.Equals(setting, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return Match(setting, carried) ?? English;
        }

        foreach (var system in systemLanguages)
        {
            if (Match(system, carried) is { } tag)
            {
                return tag;
            }
        }

        return English;
    }

    /// <summary>
    /// The carried language that serves <paramref name="requested"/>, a BCP 47 tag
    /// such as <c>zh-CN</c>, <c>zh-Hans-SG</c> or <c>de_AT.UTF-8</c>; null for none.
    /// </summary>
    public static string? Match(string requested) => Match(requested, Tags.Value);

    /// <summary><see cref="Match(string)"/> against a given set of carried tags.</summary>
    public static string? Match(string requested, IReadOnlyList<string> carried)
    {
        var tag = Normalize(requested);
        if (tag.Length == 0)
        {
            return null;
        }

        if (string.Equals(tag, Pseudo, StringComparison.OrdinalIgnoreCase))
        {
            return Pseudo;
        }

        var parts = tag.Split('-');
        var language = parts[0].ToLowerInvariant();
        var candidates = new List<string> { tag };

        // Chinese is chosen by script, not by region: Taiwan, Hong Kong and Macao
        // read Traditional, everywhere else Simplified.
        if (language == "zh")
        {
            var traditional = parts.Skip(1).Any(p =>
                p.Equals("Hant", StringComparison.OrdinalIgnoreCase) ||
                p.Equals("TW", StringComparison.OrdinalIgnoreCase) ||
                p.Equals("HK", StringComparison.OrdinalIgnoreCase) ||
                p.Equals("MO", StringComparison.OrdinalIgnoreCase));
            candidates.Add(traditional ? "zh-Hant" : "zh-Hans");
        }

        if (parts.Length > 2)
        {
            candidates.Add(parts[0] + "-" + parts[1]);
        }

        candidates.Add(language);

        foreach (var candidate in candidates)
        {
            if (carried.FirstOrDefault(t => string.Equals(t, candidate, StringComparison.OrdinalIgnoreCase)) is { } hit)
            {
                return hit;
            }
        }

        // A file for a regional variant still serves its language (pt-BR for pt), but
        // never across Chinese scripts: Traditional readers are better served by English
        // than by Simplified they may not read.
        return language == "zh"
            ? null
            : carried.FirstOrDefault(t => t.StartsWith(language + "-", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The string for <paramref name="key"/>; called by <see cref="Strings"/>.</summary>
    public static string Get(string key, string english)
    {
        if (_pseudo)
        {
            return Pseudolocalize(english);
        }

        return _table is not null && _table.TryGetValue(key, out var value) ? value : english;
    }

    /// <summary>
    /// The form of plural <paramref name="stem"/> for <paramref name="count"/>:
    /// the loaded language's category (<see cref="PluralRules"/>), then its
    /// <c>_other</c>, then English.
    /// </summary>
    public static string Plural(string stem, long count, string englishOne, string englishOther)
    {
        if (_pseudo)
        {
            return Pseudolocalize(count == 1 ? englishOne : englishOther);
        }

        if (_table is not null)
        {
            var category = PluralRules.Category(Language, count);
            if (_table.TryGetValue(stem + "_" + category, out var form) ||
                _table.TryGetValue(stem + "_other", out form))
            {
                return form;
            }
        }

        return count == 1 ? englishOne : englishOther;
    }

    /// <summary>
    /// Fills <c>{name}</c> placeholders by name, so a translation may reorder them.
    /// Numbers are written invariantly; a placeholder with no value stays as written.
    /// </summary>
    public static string Format(string template, string[] names, params object?[] values)
    {
        if (template.IndexOf('{') < 0)
        {
            return template;
        }

        var sb = new StringBuilder(template.Length + 16);
        var i = 0;
        while (i < template.Length)
        {
            var open = template.IndexOf('{', i);
            if (open < 0)
            {
                sb.Append(template, i, template.Length - i);
                break;
            }

            var close = template.IndexOf('}', open + 1);
            if (close < 0)
            {
                sb.Append(template, i, template.Length - i);
                break;
            }

            sb.Append(template, i, open - i);
            var name = template.AsSpan(open + 1, close - open - 1);
            var index = -1;
            for (var n = 0; n < names.Length; n++)
            {
                if (name.SequenceEqual(names[n]))
                {
                    index = n;
                    break;
                }
            }

            if (index >= 0 && index < values.Length)
            {
                sb.Append(Convert.ToString(values[index], CultureInfo.InvariantCulture));
                i = close + 1;
            }
            else
            {
                sb.Append('{');
                i = open + 1;
            }
        }

        return sb.ToString();
    }

    /// <summary>Reads a .resx file's string entries; the parser the tests use too.</summary>
    public static Dictionary<string, string> Read(Stream stream)
    {
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
        });

        string? name = null;
        var skip = false;
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (reader.Name == "data")
            {
                name = reader.GetAttribute("name");
                skip = reader.GetAttribute("type") is not null;
            }
            else if (reader.Name == "value" && name is not null && !skip)
            {
                table[name] = reader.ReadElementContentAsString();
                name = null;
            }
        }

        return table;
    }

    /// <summary>The pseudo-locale's rendering of an English string.</summary>
    public static string Pseudolocalize(string english)
    {
        var sb = new StringBuilder(english.Length + 2);
        sb.Append('[');
        var depth = 0;
        foreach (var c in english)
        {
            // Placeholders and the gesture glyphs stay as they are.
            if (c == '{')
            {
                depth++;
            }
            else if (c == '}' && depth > 0)
            {
                depth--;
                sb.Append(c);
                continue;
            }

            sb.Append(depth > 0 ? c : Accent(c));
        }

        return sb.Append(']').ToString();
    }

    private static char Accent(char c) => c switch
    {
        'a' => 'á', 'c' => 'ç', 'e' => 'é', 'i' => 'í', 'n' => 'ñ', 'o' => 'ö', 's' => 'š', 'u' => 'ü', 'y' => 'ý', 'z' => 'ž',
        'A' => 'Å', 'C' => 'Ç', 'E' => 'É', 'I' => 'Í', 'N' => 'Ñ', 'O' => 'Ö', 'S' => 'Š', 'U' => 'Ü', 'Y' => 'Ý', 'Z' => 'Ž',
        _ => c,
    };

    private static string Normalize(string tag)
    {
        // POSIX locales: de_AT.UTF-8@euro → de-AT.
        var cut = tag.IndexOfAny(['.', '@']);
        if (cut >= 0)
        {
            tag = tag[..cut];
        }

        tag = tag.Replace('_', '-').Trim();
        return tag is "C" or "POSIX" ? "" : tag;
    }

    private static IReadOnlyList<string> FindTags() =>
    [
        English,
        .. typeof(Localizer).Assembly.GetManifestResourceNames()
            .Where(r => r.StartsWith(ResourcePrefix, StringComparison.Ordinal) && r.EndsWith(ResourceSuffix, StringComparison.Ordinal))
            .Select(r => r[ResourcePrefix.Length..^ResourceSuffix.Length])
            .Order(StringComparer.Ordinal),
    ];

    private static string? NativeNameOf(string tag)
    {
        using var stream = typeof(Localizer).Assembly.GetManifestResourceStream(ResourcePrefix + tag + ResourceSuffix);
        var name = stream is null ? null : Read(stream).GetValueOrDefault("_LanguageName");
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }
}

/// <summary>A language this build carries: its tag and its name in itself (简体中文).</summary>
public sealed record LanguageInfo(string Tag, string NativeName);
