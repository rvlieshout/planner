using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Planner.Contracts.Common;

/// <summary>Distinguishes "field absent from the PATCH body" from "field explicitly set to null".
/// Without this, a JSON PATCH cannot express "clear the assignee" separately from "leave it alone".</summary>
[JsonConverter(typeof(OptionalConverterFactory))]
public readonly struct Optional<T>
{
    private readonly T? _value;

    private Optional(T? value)
    {
        _value = value;
        IsSet = true;
    }

    /// <summary>True when the property was present in the request body, even if its value was null.</summary>
    public bool IsSet { get; }

    public T? Value => _value;

    public static Optional<T> From(T? value) => new(value);

    public bool TryGet([MaybeNull] out T? value)
    {
        value = _value;
        return IsSet;
    }

    /// <summary>Applies the patch to <paramref name="current"/>, returning it unchanged when absent.</summary>
    public T? Or(T? current) => IsSet ? _value : current;
}

public sealed class OptionalConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(Optional<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var valueType = typeToConvert.GetGenericArguments()[0];
        return (JsonConverter)Activator.CreateInstance(
            typeof(OptionalConverter<>).MakeGenericType(valueType))!;
    }

    private sealed class OptionalConverter<T> : JsonConverter<Optional<T>>
    {
        public override Optional<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => Optional<T>.From(JsonSerializer.Deserialize<T>(ref reader, options));

        public override void Write(Utf8JsonWriter writer, Optional<T> value, JsonSerializerOptions options)
        {
            if (!value.IsSet)
            {
                writer.WriteNullValue();
                return;
            }

            JsonSerializer.Serialize(writer, value.Value, options);
        }
    }
}
