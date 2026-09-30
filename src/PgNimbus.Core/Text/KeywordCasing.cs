namespace PgNimbus.Core.Text;

/// <summary>The letter case completion writes keywords in (docs/dev/design/sql-completion-audit-2.md F02, §6.7).</summary>
public enum KeywordCase
{
    /// <summary>The case the user is typing in: <c>tr</c> → <c>true</c>, <c>SEL</c> → <c>SELECT</c>.</summary>
    AsTyped,
    Upper,
    Lower,
}

/// <summary>How a keyword row is written for <see cref="KeywordCase"/>.</summary>
public static class KeywordCasing
{
    /// <summary>
    /// <paramref name="keyword"/> (as the list spells it, upper case) in
    /// <paramref name="mode"/>. As typed: all-lowercase letters typed mean
    /// lower case; anything else — capitals, mixed, or nothing typed yet — the
    /// usual upper case. Only the letters change: <c>FILTER (WHERE )</c> →
    /// <c>filter (where )</c>.
    /// </summary>
    public static string Apply(string keyword, string typed, KeywordCase mode) => mode switch
    {
        KeywordCase.Upper => keyword.ToUpperInvariant(),
        KeywordCase.Lower => keyword.ToLowerInvariant(),
        _ => typed.Any(char.IsLetter) && !typed.Any(char.IsUpper) ? keyword.ToLowerInvariant() : keyword.ToUpperInvariant(),
    };
}
