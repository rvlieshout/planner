namespace Planner.Domain.Common;

/// <summary>
/// Lexicographic rank keys: the position of a row in a hand-ordered sequence — a board column, a
/// team's projects, a project's milestones, a team's workflow states — as a string that sorts
/// byte-wise into that order.
///
/// <para>Between any two keys there is always another, so moving a row writes that one row and nothing
/// else, and unlike a <c>double</c> midpoint the space never runs out: a gap that has been split too
/// often simply yields a longer key.</para>
///
/// <para>The scheme is the fractional indexing of Figma and Rocicorp's <c>fractional-indexing</c>, over
/// base 62 (<c>0-9A-Za-z</c>, which is ASCII order). A key is an <em>integer part</em> whose first
/// character gives its length — <c>a0</c>…<c>az</c>, then <c>b00</c>…<c>bzz</c>, and upper case for the
/// negatives — followed by an optional <em>fraction</em> that never ends in <c>0</c>. Appending at an
/// end increments the integer, so a column that only ever grows gets keys that grow logarithmically;
/// inserting between two keys extends the fraction.</para>
///
/// <para>The ordering is ordinal: Postgres stores these columns with <c>COLLATE "C"</c>, and every
/// in-memory comparison uses <see cref="StringComparer.Ordinal"/>. The web client carries a line-for-line
/// port in <c>client/src/lib/rank.ts</c>, so the key it shows optimistically is the key the server
/// writes.</para>
/// </summary>
public static class Rank
{
    private const string Digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    private const char Zero = '0';
    private const char Nine = 'z';

    /// <summary>The key of the first row of an empty sequence.</summary>
    public const string First = "a0";

    /// <summary>Longest key the API accepts from a client. Keys the server makes are not held to it —
    /// refusing to place a row next to a long key would break the list — but stay far shorter in
    /// practice: one character is added for roughly every six drops into the very same gap.</summary>
    public const int MaxLength = 128;

    /// <summary>The one integer part with nothing before it, which a key may not be on its own.</summary>
    private static readonly string Smallest = "A" + new string(Zero, 26);

    public static readonly StringComparer Comparer = StringComparer.Ordinal;

    /// <summary>A key strictly between <paramref name="after"/> and <paramref name="before"/>. Either may
    /// be null for an open end; both null gives <see cref="First"/>.</summary>
    /// <exception cref="ArgumentException">A key is malformed, or <paramref name="after"/> does not sort
    /// strictly before <paramref name="before"/>.</exception>
    public static string Between(string? after, string? before)
    {
        if (after is not null) Validate(after);
        if (before is not null) Validate(before);

        if (after is not null && before is not null && string.CompareOrdinal(after, before) >= 0)
        {
            throw new ArgumentException($"Rank '{after}' does not sort before '{before}'.");
        }

        if (after is null)
        {
            if (before is null) return First;

            var integer = IntegerPart(before);
            var fraction = before[integer.Length..];

            if (integer == Smallest) return integer + Midpoint("", fraction);
            if (string.CompareOrdinal(integer, before) < 0) return integer;

            return Decrement(integer) ?? throw new ArgumentException("There is no rank before that one.");
        }

        if (before is null)
        {
            var integer = IntegerPart(after);
            return Increment(integer) ?? integer + Midpoint(after[integer.Length..], null);
        }

        var afterInteger = IntegerPart(after);
        var beforeInteger = IntegerPart(before);

        if (afterInteger == beforeInteger)
        {
            return afterInteger + Midpoint(after[afterInteger.Length..], before[beforeInteger.Length..]);
        }

        var next = Increment(afterInteger) ?? throw new ArgumentException("There is no rank after that one.");
        return string.CompareOrdinal(next, before) < 0 ? next : afterInteger + Midpoint(after[afterInteger.Length..], null);
    }

    /// <summary><paramref name="count"/> ascending keys strictly between two bounds, spread so that later
    /// inserts anywhere among them stay short.</summary>
    public static IReadOnlyList<string> Sequence(int count, string? after = null, string? before = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        var keys = new List<string>(count);
        Fill(keys, after, before, count);
        return keys;
    }

    private static void Fill(List<string> keys, string? after, string? before, int count)
    {
        if (count == 0) return;

        if (before is null)
        {
            var key = Between(after, null);
            keys.Add(key);
            for (var i = 1; i < count; i++) keys.Add(key = Between(key, null));
            return;
        }

        if (after is null)
        {
            var reversed = new string[count];
            var key = Between(null, before);
            reversed[count - 1] = key;
            for (var i = count - 2; i >= 0; i--) reversed[i] = key = Between(null, key);
            keys.AddRange(reversed);
            return;
        }

        var half = count / 2;
        var middle = Between(after, before);
        Fill(keys, after, middle, half);
        keys.Add(middle);
        Fill(keys, middle, before, count - half - 1);
    }

    /// <summary>Whether a value is a well-formed key: base-62 digits, an integer part as long as its head
    /// says, and a fraction that does not end in <c>0</c>. Length is not part of it; see
    /// <see cref="MaxLength"/>.</summary>
    public static bool IsValid(string? key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        if (key.Any(c => Digits.IndexOf(c) < 0)) return false;

        var length = IntegerLength(key[0]);
        if (length == 0 || length > key.Length || key == Smallest) return false;

        // A bare integer may end in 0 (a0 is the first key); a fraction may not, or nothing could ever be
        // placed between it and the same fraction without that 0.
        return length == key.Length || key[^1] != Zero;
    }

    private static void Validate(string key)
    {
        if (!IsValid(key)) throw new ArgumentException($"'{key}' is not a rank key.");
    }

    /// <summary>A string strictly between two fractions, read as base-62 digits after a radix point.
    /// <paramref name="after"/> may be empty (zero) and <paramref name="before"/> null (one).</summary>
    private static string Midpoint(string after, string? before)
    {
        if (before is not null)
        {
            // Skip the digits the two share; the answer shares them too.
            var shared = 0;
            while ((shared < after.Length ? after[shared] : Zero) == before[shared]) shared++;

            if (shared > 0) return before[..shared] + Midpoint(after[Math.Min(shared, after.Length)..], before[shared..]);
        }

        var low = after.Length > 0 ? Digits.IndexOf(after[0]) : 0;
        var high = before is not null ? Digits.IndexOf(before[0]) : Digits.Length;

        if (high - low > 1)
        {
            return Digits[(int)Math.Round(0.5 * (low + high), MidpointRounding.AwayFromZero)].ToString();
        }

        // Adjacent digits: take the upper one alone if more follows it, or else keep the lower one and
        // go a digit deeper.
        if (before is { Length: > 1 }) return before[..1];

        return Digits[low] + Midpoint(after.Length > 0 ? after[1..] : "", null);
    }

    private static int IntegerLength(char head) => head switch
    {
        >= 'a' and <= 'z' => head - 'a' + 2,
        >= 'A' and <= 'Z' => 'Z' - head + 2,
        _ => 0
    };

    private static string IntegerPart(string key) => key[..IntegerLength(key[0])];

    private static string? Increment(string integer)
    {
        var head = integer[0];
        var digits = integer[1..].ToCharArray().ToList();

        for (var i = digits.Count - 1; i >= 0; i--)
        {
            var next = Digits.IndexOf(digits[i]) + 1;
            if (next < Digits.Length)
            {
                digits[i] = Digits[next];
                return head + new string([.. digits]);
            }

            digits[i] = Zero;
        }

        // Carried out of every digit: the integer part grows (or shrinks, for a negative) by one.
        if (head == 'Z') return "a" + Zero;
        if (head == 'z') return null;

        var nextHead = (char)(head + 1);
        if (nextHead > 'a') digits.Add(Zero);
        else digits.RemoveAt(digits.Count - 1);

        return nextHead + new string([.. digits]);
    }

    private static string? Decrement(string integer)
    {
        var head = integer[0];
        var digits = integer[1..].ToCharArray().ToList();

        for (var i = digits.Count - 1; i >= 0; i--)
        {
            var next = Digits.IndexOf(digits[i]) - 1;
            if (next >= 0)
            {
                digits[i] = Digits[next];
                return head + new string([.. digits]);
            }

            digits[i] = Nine;
        }

        if (head == 'a') return "Z" + Nine;
        if (head == 'A') return null;

        var nextHead = (char)(head - 1);
        if (nextHead < 'Z') digits.Add(Nine);
        else digits.RemoveAt(digits.Count - 1);

        return nextHead + new string([.. digits]);
    }
}
