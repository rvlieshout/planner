using Planner.Domain.Common;

namespace Planner.Api.Checks;

/// <summary>Covers <see cref="Rank"/>: the keys themselves against vectors from Rocicorp's
/// <c>fractional-indexing</c> test suite — an independent implementation of the same scheme — and the
/// properties a hand-ordered list depends on under thousands of random moves. The web client's port is
/// held to the same vectors in <c>client/tests/rank.test.mjs</c>.</summary>
public static class RankChecks
{
    /// <summary>(after, before, expected); a null expected means the pair must be refused.</summary>
    private static readonly (string? After, string? Before, string? Expected)[] Vectors =
    [
        (null, null, "a0"),
        (null, "a0", "Zz"),
        (null, "Zz", "Zy"),
        ("a0", null, "a1"),
        ("a1", null, "a2"),
        ("a0", "a1", "a0V"),
        ("a1", "a2", "a1V"),
        ("a0V", "a1", "a0l"),
        ("Zz", "a0", "ZzV"),
        ("Zz", "a1", "a0"),
        (null, "Y00", "Xzzz"),
        ("bzz", null, "c000"),
        ("a0", "a0V", "a0G"),
        ("a0", "a0G", "a08"),
        ("b125", "b129", "b127"),
        ("a0", "a1V", "a1"),
        ("Zz", "a01", "a0"),
        (null, "a0V", "a0"),
        (null, "b999", "b99"),
        (null, "A000000000000000000000000001", "A000000000000000000000000000V"),
        ("zzzzzzzzzzzzzzzzzzzzzzzzzzy", null, "zzzzzzzzzzzzzzzzzzzzzzzzzzz"),
        ("zzzzzzzzzzzzzzzzzzzzzzzzzzz", null, "zzzzzzzzzzzzzzzzzzzzzzzzzzzV"),
        (null, "A00000000000000000000000000", null),
        ("a00", null, null),
        ("a00", "a1", null),
        ("0", "1", null),
        ("a1", "a0", null),
        ("a1", "a1", null)
    ];

    public static void Run(Action<bool, string> check)
    {
        foreach (var (after, before, expected) in Vectors)
        {
            var label = $"between({after ?? "null"}, {before ?? "null"})";

            if (expected is null)
            {
                var refused = false;
                try { Rank.Between(after, before); }
                catch (ArgumentException) { refused = true; }
                check(refused, $"{label} is refused");
            }
            else
            {
                var actual = Rank.Between(after, before);
                check(actual == expected, $"{label} is {expected} (got {actual})");
            }
        }

        // The keys the migration gives existing rows: d followed by four digits.
        check(Rank.IsValid("d0001") && Rank.IsValid("d00zz"), "Migrated keys are well formed");
        check(Rank.Between("d0001", "d0002") == "d0001V", "A key fits between two migrated neighbours");
        check(Rank.Between("d00zz", null) == "d0100", "Appending after a migrated key increments it");

        var sequence = Rank.Sequence(100);
        check(sequence.Count == 100 && sequence.Zip(sequence.Skip(1)).All(p => Rank.Comparer.Compare(p.First, p.Second) < 0),
            "A generated sequence is strictly ascending");
        check(sequence.Take(62).All(key => key.Length == 2) && sequence.Skip(62).All(key => key.Length == 3),
            "Appended keys run a0…az, then b00 onwards");

        var inner = Rank.Sequence(50, "a0", "a1");
        check(inner.All(key => Rank.Comparer.Compare(key, "a0") > 0 && Rank.Comparer.Compare(key, "a1") < 0)
              && inner.Zip(inner.Skip(1)).All(p => Rank.Comparer.Compare(p.First, p.Second) < 0),
            "A sequence between two bounds stays inside them, ascending");

        RandomMoves(check);
        AppendGrowth(check);
    }

    /// <summary>Drags rows around a list at random, the way a board is used, and checks that the keys
    /// always put it in exactly the order the drags produced.</summary>
    private static void RandomMoves(Action<bool, string> check)
    {
        var random = new Random(20260922);
        var list = new List<(int Id, string Rank)>();
        var ordered = true;
        var valid = true;

        for (var id = 0; id < 5000; id++)
        {
            var moving = list.Count > 0 && random.Next(3) > 0 ? random.Next(list.Count) : -1;
            var row = moving >= 0 ? list[moving].Id : id;
            if (moving >= 0) list.RemoveAt(moving);

            var at = random.Next(list.Count + 1);
            var rank = Rank.Between(at > 0 ? list[at - 1].Rank : null, at < list.Count ? list[at].Rank : null);
            list.Insert(at, (row, rank));

            valid &= Rank.IsValid(rank);
        }

        var sorted = list.OrderBy(r => r.Rank, Rank.Comparer).Select(r => r.Id);
        ordered &= sorted.SequenceEqual(list.Select(r => r.Id));

        check(valid, "Every key made by 5,000 random moves is well formed");
        check(ordered, "Sorting by key reproduces the order 5,000 random moves produced");
        check(list.Max(r => r.Rank.Length) <= 12, $"Keys stay short under random moves (longest {list.Max(r => r.Rank.Length)})");

        // The worst case: every drop lands directly under the same card, so the gap keeps halving.
        var above = "a0";
        var below = "a1";
        for (var i = 0; i < 300; i++) below = Rank.Between(above, below);
        check(below.Length < 64, $"300 drops into the same gap still make a short key ({below.Length} characters)");

        // Far past the length a client may send: the server must still place rows around its own keys.
        for (var i = 0; i < 1000; i++) below = Rank.Between(above, below);
        check(below.Length > Rank.MaxLength && Rank.IsValid(below) &&
              string.CompareOrdinal(Rank.Between(above, below), below) < 0 &&
              string.CompareOrdinal(Rank.Between(below, "a1"), below) > 0,
            $"A {below.Length}-character key is still placed around");
    }

    private static void AppendGrowth(Action<bool, string> check)
    {
        string? last = null;
        for (var i = 0; i < 100_000; i++) last = Rank.Between(last, null);
        check(last!.Length <= 4, $"100,000 appends end on a key of {last.Length} characters");
    }
}
