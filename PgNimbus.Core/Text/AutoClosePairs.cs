namespace PgNimbus.Core.Text;

/// <summary>
/// Decides what typing <c>(</c>, <c>)</c>, <c>'</c>, or <c>"</c> should do in
/// the SQL editor beyond inserting the character: auto-insert the matching
/// closer (caret staying between the pair), or step over a closer that is
/// already there instead of doubling it. Pure decision logic — the editor
/// wiring supplies the caret's string/comment state (it already computes that
/// per keystroke for completion) and applies the verdict to the document.
/// </summary>
public static class AutoClosePairs
{
    public enum Verdict
    {
        /// <summary>Nothing special — let the character insert as typed.</summary>
        None,
        /// <summary>Insert the matching closer after the typed character; the caret stays between them.</summary>
        InsertPair,
        /// <summary>Skip over the identical character already at the caret instead of inserting a second one.</summary>
        TypeOver,
    }

    /// <summary>
    /// The verdict for <paramref name="typed"/> about to be inserted at
    /// <paramref name="caret"/> in <paramref name="text"/> (both reflect the
    /// state <b>before</b> insertion). <paramref name="inStringOrComment"/> is
    /// the caret's literal/comment state: no pairing happens inside prose —
    /// except stepping over a quote, which is exactly how a string closes.
    /// </summary>
    public static Verdict Decide(string text, int caret, char typed, bool inStringOrComment)
    {
        var next = caret >= 0 && caret < text.Length ? text[caret] : '\0';

        switch (typed)
        {
            case '(':
                return !inStringOrComment && ClosingHereReadsCleanly(next) ? Verdict.InsertPair : Verdict.None;

            case ')':
                return !inStringOrComment && next == ')' ? Verdict.TypeOver : Verdict.None;

            case '\'' or '"':
                if (next == typed)
                {
                    // Only a caret *inside* the literal is looking at its
                    // closer; outside, the neighboring quote opens a different
                    // literal and stepping over it would jump into it.
                    return inStringOrComment ? Verdict.TypeOver : Verdict.None;
                }

                // A quote inside a string/comment is content ('it''s, a doc
                // comment's apostrophe), never the start of a new literal.
                return !inStringOrComment && ClosingHereReadsCleanly(next) ? Verdict.InsertPair : Verdict.None;

            default:
                return Verdict.None;
        }
    }

    /// <summary>The closer for an opener that earned <see cref="Verdict.InsertPair"/>.</summary>
    public static char CloserFor(char opener) => opener == '(' ? ')' : opener;

    // Auto-closing is only helpful when the pair lands before a boundary (end
    // of text, whitespace, an operator, a closer…). Typing an opener directly
    // in front of a word or another quote means the user is editing existing
    // text — wrapping is their call, not ours.
    private static bool ClosingHereReadsCleanly(char next) =>
        next == '\0'
        || (!char.IsLetterOrDigit(next) && next is not ('_' or '$' or '\'' or '"'));
}
