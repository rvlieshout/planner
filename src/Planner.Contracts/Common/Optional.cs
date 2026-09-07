using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Planner.Contracts.Common;

/// <summary>Non-generic view of <see cref="Optional{T}"/>, so serialization can ask whether a value was
/// set without knowing its type.</summary>
public interface IOptional
{
    bool IsSet { get; }
}

/// <summary>Distinguishes "field absent from the PATCH body" from "field explicitly set to null".
/// Without this, a JSON PATCH cannot express "clear the assignee" separately from "leave it alone".</summary>
[JsonConverter(typeof(OptionalConverterFactory))]
public readonly struct Optional<T> : IOptional
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

/// <summary>Makes unset <see cref="Optional{T}"/> properties disappear from the JSON entirely.
///
/// A converter cannot do this on its own: by the time it runs, the property name has already been
/// written, so the best it could manage is an explicit null — which is precisely the opposite of what
/// "absent" means to a PATCH endpoint. Without this, a client sending one changed field would clear
/// every other field on the entity.</summary>
public static class OptionalJson
{
    public static void IgnoreUnset(JsonTypeInfo typeInfo)
    {
        foreach (var property in typeInfo.Properties)
        {
            if (!property.PropertyType.IsGenericType ||
                property.PropertyType.GetGenericTypeDefinition() != typeof(Optional<>))
            {
                continue;
            }

            property.ShouldSerialize = (_, value) => value is IOptional { IsSet: true };
        }
    }

    /// <summary>Serializer options that write PATCH bodies correctly. Web defaults plus the modifier.</summary>
    public static JsonSerializerOptions CreateOptions(params JsonConverter[] converters)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { IgnoreUnset } }
        };

        foreach (var converter in converters)
        {
            options.Converters.Add(converter);
        }

        return options;
    }
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
