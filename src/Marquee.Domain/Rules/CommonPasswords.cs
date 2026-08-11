using System.Reflection;

namespace Marquee.Domain.Rules;

/// <summary>
/// The "obvious top-N" blocklist from issue #27, matched after normalisation rather than literally.
///
/// The list itself is data, not a guess: <c>Resources/common-passwords-top10000.txt</c> is the top
/// 10,000 passwords by observed real-world frequency from the Xato "10 million passwords" dataset
/// (Mark Burnett, 2015 — a merge of public breach corpora, RockYou among them), retrieved from
/// SecLists (github.com/danielmiessler/SecLists,
/// <c>Passwords/Common-Credentials/xato-net-10-million-passwords-10000.txt</c>). A handful of entries
/// under "Marquee's own vocabulary" below are the one part that cannot come from a generic breach
/// corpus — this app's own words are the first thing someone here would reach for, and no dataset of
/// passwords leaked from other products would ever contain them.
///
/// A literal list is close to useless once a minimum length and a required digit are in play: nobody
/// can submit "password" anyway, so the list would only ever catch what other rules already caught.
/// What people actually submit is "password" with the minimum decoration needed to satisfy the other
/// rules — "Password123!", "P@ssw0rd2026". Normalising to the base word before matching is what lets
/// ten thousand entries stand in for the much larger set of variants derived from them — matching
/// against the frequency list works here specifically because the list already contains most of
/// those base words on their own, undecorated (rank #2 is "password" itself), so a decorated
/// candidate that trims and reduces down to one of those still lands on an exact entry.
///
/// Matching is exact against the normalised form, never a substring: "monkey" appearing inside a
/// passphrase says nothing about that passphrase, and a substring rule would reject good passwords
/// for containing a common word, which is the opposite of the intent.
/// </summary>
public static class CommonPasswords
{
    /// <summary>
    /// Not sourced from breach data — these are Marquee's own vocabulary, the words someone here
    /// would reach for first ("marquee12345", "premiere2026"). Kept deliberately short: this is
    /// meant to catch the obvious, not to guess at every derivative.
    /// </summary>
    private static readonly string[] MarqueeVocabulary =
    [
        "marquee", "marqueeapp", "premiere", "premieres", "clap", "claps", "cinema", "movie",
        "movies", "film", "films", "hollywood", "popcorn", "boxoffice", "director", "screening",
    ];

    /// <summary>
    /// Digits and symbols commonly standing in for letters. Applied before letters-only reduction,
    /// so "pa55w0rd" and "p@ssword" both reduce to the same entry as "password".
    /// </summary>
    private static readonly (char From, char To)[] LeetMap =
    [
        ('0', 'o'), ('3', 'e'), ('4', 'a'), ('5', 's'), ('7', 't'), ('8', 'b'), ('9', 'g'),
        ('@', 'a'), ('$', 's'), ('!', 'i'), ('|', 'l'), ('+', 't'),
    ];

    private static readonly HashSet<string> Blocked = Load();

    /// <summary>True when the password is one of the well-known ones, in any of its usual disguises.</summary>
    public static bool Contains(string password)
    {
        if (string.IsNullOrEmpty(password))
            return false;

        var lowered = password.ToLowerInvariant();

        if (Blocked.Contains(lowered))
            return true;

        // Two source strings, because a digit at the edge of a password and a digit inside one mean
        // different things. "password1234" is a word with a number stuck on the end; "pa55w0rd" is
        // the same word with letters written as digits. Reading both as leet turns "password1234"
        // into "passwordiea" and finds nothing, so the trimmed form is tried alongside the whole one.
        //
        // The "1" is ambiguous between i and l ("1loveyou" against "he11o"). Picking one loses the
        // other, so both readings are tried; every other substitution has a single sensible reading.
        foreach (var source in new[] { lowered, TrimDecoration(lowered) })
        foreach (var oneIs in new[] { 'i', 'l' })
        {
            var reduced = Reduce(source, oneIs);
            if (reduced.Length > 0 && Blocked.Contains(reduced))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Reads the embedded frequency list plus <see cref="MarqueeVocabulary"/> into one set. Lines
    /// starting with "#" are the file's own attribution header, not data.
    /// </summary>
    private static HashSet<string> Load()
    {
        var assembly = typeof(CommonPasswords).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith("common-passwords-top10000.txt", StringComparison.Ordinal));

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);

        var blocked = new HashSet<string>(StringComparer.Ordinal);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0 && !trimmed.StartsWith('#'))
                blocked.Add(trimmed);
        }

        foreach (var word in MarqueeVocabulary)
            blocked.Add(word);

        return blocked;
    }

    /// <summary>
    /// Drops the leading and trailing runs of anything that is not a letter — an appended year, a
    /// "123", a final "!". What is left is the word the password was built around, if it was built
    /// around one.
    /// </summary>
    private static string TrimDecoration(string lowered)
    {
        var start = 0;
        var end = lowered.Length - 1;

        while (start <= end && !char.IsLetter(lowered[start])) start++;
        while (end >= start && !char.IsLetter(lowered[end])) end--;

        return start > end ? string.Empty : lowered[start..(end + 1)];
    }

    /// <summary>
    /// Undo the common letter-for-symbol substitutions, then drop everything that is still not a
    /// letter — interior separators, spaces, stray digits.
    /// </summary>
    private static string Reduce(string lowered, char oneIs)
    {
        var chars = new List<char>(lowered.Length);

        foreach (var raw in lowered)
        {
            var c = raw == '1' ? oneIs : Substitute(raw);
            if (char.IsLetter(c))
                chars.Add(c);
        }

        return new string(chars.ToArray());
    }

    private static char Substitute(char c)
    {
        foreach (var (from, to) in LeetMap)
            if (c == from)
                return to;
        return c;
    }
}
