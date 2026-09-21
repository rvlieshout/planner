using System.Text.Json;
using System.Text.Json.Serialization;

namespace Planner.Contracts.Common;

/// <summary>Writes every <see cref="Guid"/> in a payload as base58 and reads one back.
///
/// Registered globally rather than hung off each property with an attribute: ids reach the wire from
/// DTO fields, from dictionary keys and from the odd anonymous object, and a global converter cannot
/// be forgotten on the next contract someone adds. Entities and queries keep working in
/// <see cref="Guid"/>, so this is the only place the two representations meet.</summary>
public sealed class Base58GuidConverter : JsonConverter<Guid>
{
    public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Parse(ref reader);

    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToBase58());

    public override Guid ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Parse(ref reader);

    public override void WriteAsPropertyName(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) =>
        writer.WritePropertyName(value.ToBase58());

    internal static Guid Parse(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.String && reader.TokenType != JsonTokenType.PropertyName)
        {
            throw new JsonException($"Expected an identifier string but found {reader.TokenType}.");
        }

        var text = reader.GetString();

        return Base58.TryParseId(text, out var value)
            ? value
            : throw new JsonException($"'{text}' is not a valid identifier.");
    }
}

/// <summary>The nullable counterpart. System.Text.Json unwraps <c>Guid?</c> to the
/// <see cref="Guid"/> converter for properties, but not everywhere — a <c>Guid?</c> inside
/// <c>Optional&lt;T&gt;</c>, an array or an anonymous object is serialized as its own type — so the
/// pair is registered together.</summary>
public sealed class NullableBase58GuidConverter : JsonConverter<Guid?>
{
    public override Guid? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : Base58GuidConverter.Parse(ref reader);

    public override void Write(Utf8JsonWriter writer, Guid? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStringValue(value.Value.ToBase58());
    }
}
