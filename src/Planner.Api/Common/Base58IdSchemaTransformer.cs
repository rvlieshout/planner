using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Planner.Contracts.Common;

namespace Planner.Api.Common;

/// <summary>Describes identifiers the way the API actually writes them.
///
/// Left alone, every <see cref="Guid"/> in the document is advertised as <c>format: uuid</c> with a
/// hyphenated example — which no endpoint returns and which a generated client would validate
/// against. This replaces that with the base58 shape, so the Scalar page, and anything generating
/// code from the document, gets the real thing.</summary>
public sealed class Base58IdSchemaTransformer : IOpenApiSchemaTransformer
{
    /// <summary>The 22 characters of the base58 alphabet, with <c>0 O I l</c> absent.</summary>
    private const string IdPattern = "^[1-9A-HJ-NP-Za-km-z]{22}$";

    private static readonly JsonNode Example = JsonValue.Create(
        Guid.Parse("019205f7-0c3e-7b6a-9f21-4d8c5e6a1b37").ToBase58())!;

    public Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        var declared = context.JsonTypeInfo.Type;

        if ((Nullable.GetUnderlyingType(declared) ?? declared) != typeof(Guid))
        {
            return Task.CompletedTask;
        }

        // The generator describes a Guid as a bare `format: uuid`, and a Guid? as nothing at all, so
        // the type has to be stated here or the constraints below apply to nothing.
        schema.Type = declared == typeof(Guid?) || schema.Type?.HasFlag(JsonSchemaType.Null) == true
            ? JsonSchemaType.String | JsonSchemaType.Null
            : JsonSchemaType.String;

        schema.Format = "base58";
        schema.Pattern = IdPattern;
        schema.MinLength = Base58.EncodedLength;
        schema.MaxLength = Base58.EncodedLength;
        schema.Example = Example;
        schema.Description = "A base58-encoded identifier, 22 characters. The canonical uuid form is " +
                             "also accepted on input.";

        return Task.CompletedTask;
    }
}
