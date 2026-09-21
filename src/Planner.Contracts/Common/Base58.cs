using System.Diagnostics.CodeAnalysis;

namespace Planner.Contracts.Common;

/// <summary>Base58 for the 128 bits of a <see cref="Guid"/>.
///
/// The database stores real uuids and every id inside the server stays a <see cref="Guid"/>; this is
/// purely the shape those ids take on the wire. Base58 buys three things over the canonical
/// hyphenated form: it is 22 characters instead of 36, it survives a double-click, a URL and a
/// spreadsheet cell unescaped, and the alphabet leaves out the four glyphs — <c>0 O I l</c> — that
/// make a hand-copied id ambiguous.
///
/// The value encoded is the uuid's 16 bytes in RFC 4122 order read as one 128-bit integer, and the
/// alphabet is Bitcoin's, so any base58 library decodes an id to the same number. The one deviation
/// is padding: the output is always 22 digits, where base58check instead writes one leading <c>1</c>
/// per leading zero byte. A client decoding with an off-the-shelf library should therefore take the
/// number and left-pad it to 16 bytes rather than trusting the decoded array's length.</summary>
public static class Base58
{
    private const string Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    /// <summary>58^22 &gt; 2^128 &gt; 58^21, so every uuid fits in 22 digits and none needs 23.</summary>
    public const int EncodedLength = 22;

    /// <summary>Reverse lookup for the printable ASCII range. -1 marks a character outside the
    /// alphabet, which includes the excluded <c>0 O I l</c>.</summary>
    private static readonly sbyte[] Digits = BuildDigits();

    private static sbyte[] BuildDigits()
    {
        var digits = new sbyte[128];
        Array.Fill(digits, (sbyte)-1);

        for (var i = 0; i < Alphabet.Length; i++)
        {
            digits[Alphabet[i]] = (sbyte)i;
        }

        return digits;
    }

    /// <summary>Encodes a uuid as exactly <see cref="EncodedLength"/> characters. Short values are
    /// left-padded with the zero digit rather than shortened, so every id a client sees is the same
    /// width and can be validated on length alone.</summary>
    public static string ToBase58(this Guid value)
    {
        Span<byte> remaining = stackalloc byte[16];
        value.TryWriteBytes(remaining, bigEndian: true, out _);

        Span<char> encoded = stackalloc char[EncodedLength];
        var next = EncodedLength;

        // Long division by 58 over the big-endian bytes, least significant digit first. `start` walks
        // past bytes that have been divided down to zero so each pass gets cheaper.
        var start = 0;
        while (true)
        {
            while (start < 16 && remaining[start] == 0)
            {
                start++;
            }

            if (start == 16)
            {
                break;
            }

            var carry = 0;
            for (var i = start; i < 16; i++)
            {
                var accumulator = (carry << 8) | remaining[i];
                remaining[i] = (byte)(accumulator / 58);
                carry = accumulator % 58;
            }

            encoded[--next] = Alphabet[carry];
        }

        while (next > 0)
        {
            encoded[--next] = Alphabet[0];
        }

        return new string(encoded);
    }

    /// <summary>Decodes an id written by <see cref="ToBase58(Guid)"/>.
    ///
    /// The width is exact rather than a maximum. Base58 would happily read "12" as the same value as
    /// the padded "1…12", and one id with several spellings is a bug waiting to happen in any client
    /// that compares them as strings. Insisting on 22 also means a truncated or mistyped id fails
    /// outright instead of quietly decoding to some other row's id.</summary>
    public static bool TryParse(ReadOnlySpan<char> text, out Guid value)
    {
        value = default;

        if (text.Length != EncodedLength)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[16];

        foreach (var character in text)
        {
            if (character >= Digits.Length || Digits[character] < 0)
            {
                return false;
            }

            var carry = (int)Digits[character];
            for (var i = 15; i >= 0; i--)
            {
                carry += bytes[i] * 58;
                bytes[i] = (byte)carry;
                carry >>= 8;
            }

            // 22 digits can address more than 2^128 values, so the top of that range has no uuid to
            // decode to. Rejecting the overflow keeps decoding injective.
            if (carry != 0)
            {
                return false;
            }
        }

        value = new Guid(bytes, bigEndian: true);
        return true;
    }

    /// <summary>Decodes an id the API issued, and also accepts the canonical hyphenated form.
    ///
    /// Leniency on the way in costs nothing — the two forms cannot be confused, one is 22 characters
    /// of base58 and the other 36 with hyphens — and it keeps older links, saved curl commands and
    /// anything holding an id from before this encoding existed working against the same routes.</summary>
    public static bool TryParseId(ReadOnlySpan<char> text, out Guid value) =>
        TryParse(text, out value) || Guid.TryParse(text, out value);

    /// <inheritdoc cref="TryParseId(ReadOnlySpan{char}, out Guid)"/>
    public static bool TryParseId([NotNullWhen(true)] string? text, out Guid value)
    {
        if (text is null)
        {
            value = default;
            return false;
        }

        return TryParseId(text.AsSpan(), out value);
    }

    /// <summary>Decodes an id or throws. For call sites that have already validated the input.</summary>
    public static Guid ParseId(ReadOnlySpan<char> text) =>
        TryParseId(text, out var value)
            ? value
            : throw new FormatException($"'{text}' is not a base58 identifier.");
}
