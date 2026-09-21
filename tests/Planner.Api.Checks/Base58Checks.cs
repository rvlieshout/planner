using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Planner.Api.Common;
using Planner.Contracts.Common;

namespace Planner.Api.Checks;

/// <summary>Covers the identifier encoding end to end: the codec itself against vectors produced by an
/// independent implementation, the JSON converters, and — over a real socket — the route constraint
/// and the binding middleware that let a base58 id reach a handler declared as a
/// <see cref="Guid"/>.</summary>
public static class Base58Checks
{
    /// <summary>Encodings computed with a separate base58 implementation, not with the code under
    /// test. The two ends of the range pin the padding and the width; the rest pin byte order.</summary>
    private static readonly (string Uuid, string Encoded)[] Vectors =
    [
        ("00000000-0000-0000-0000-000000000000", "1111111111111111111111"),
        ("00000000-0000-0000-0000-000000000001", "1111111111111111111112"),
        ("00112233-4455-6677-8899-aabbccddeeff", "11UoWww8DGaVGLtea7zU7p"),
        ("019205f7-0c3e-7b6a-9f21-4d8c5e6a1b37", "1CFM9HDkWavHzjEZuAP3qG"),
        ("ffffffff-ffff-ffff-ffff-ffffffffffff", "YcVfxkQb6JRzqk5kF2tNLv")
    ];

    public static async Task RunAsync(Action<bool, string> check)
    {
        Codec(check);
        Json(check);
        await BindingAsync(check);
    }

    private static void Codec(Action<bool, string> check)
    {
        foreach (var (uuid, encoded) in Vectors)
        {
            var id = Guid.Parse(uuid);
            check(id.ToBase58() == encoded, $"{uuid} encodes to {encoded}");
            check(Base58.TryParse(encoded, out var decoded) && decoded == id, $"{encoded} decodes to {uuid}");
        }

        var widths = true;
        var roundTrips = true;

        for (var i = 0; i < 20_000; i++)
        {
            var id = Guid.CreateVersion7();
            var encoded = id.ToBase58();

            widths &= encoded.Length == Base58.EncodedLength;
            roundTrips &= Base58.TryParse(encoded, out var decoded) && decoded == id;
        }

        check(widths, "Every id encodes to exactly 22 characters");
        check(roundTrips, "20,000 uuids survive a round trip");

        foreach (var ambiguous in new[] { "0", "O", "I", "l" })
        {
            var text = new string(ambiguous[0], Base58.EncodedLength);
            check(!Base58.TryParse(text, out _), $"'{ambiguous}' is not in the alphabet");
        }

        // 58^22 is larger than 2^128, so the top of the 22-digit range has no uuid behind it.
        check(!Base58.TryParse(new string('z', Base58.EncodedLength), out _), "Values above 2^128 are rejected");
        check(!Base58.TryParse("", out _), "An empty id is rejected");
        check(!Base58.TryParse(new string('2', 23), out _), "An over-long id is rejected");

        // Unpadded base58 would read this as a valid — and different — id.
        check(!Base58.TryParse(Vectors[3].Encoded[..21], out _), "A truncated id is rejected, not re-read");
        check(!Base58.TryParse(Vectors[3].Encoded[1..], out _), "Dropping the padding is not another spelling");

        var canonical = Guid.CreateVersion7();
        check(Base58.TryParseId(canonical.ToString(), out var parsed) && parsed == canonical,
            "The canonical uuid form is still accepted on input");
        check(!Base58.TryParseId("not-an-id", out _), "Nonsense is rejected by both forms");
    }

    private static void Json(Action<bool, string> check)
    {
        var options = OptionalJson.CreateOptions(
            new JsonStringEnumConverter(),
            new Base58GuidConverter(),
            new NullableBase58GuidConverter());

        var id = Guid.Parse("019205f7-0c3e-7b6a-9f21-4d8c5e6a1b37");
        var payload = new Payload(id, null, Optional<Guid?>.From(id), [id, id]);
        var json = JsonSerializer.Serialize(payload, options);

        check(!json.Contains(id.ToString()), "No canonical uuid survives serialization");
        check(json.Contains(id.ToBase58()), "Ids are written as base58");
        check(json.Contains("\"assignee\":null"), "A null id stays null");

        var restored = JsonSerializer.Deserialize<Payload>(json, options)!;
        check(restored == payload with { Labels = restored.Labels }, "A payload round trips");
        check(restored.Labels.SequenceEqual(payload.Labels), "Ids inside a collection round trip");

        var canonical = $$"""{"id":"{{id}}","assignee":null,"project":null,"labels":[]}""";
        check(JsonSerializer.Deserialize<Payload>(canonical, options)!.Id == id,
            "A body holding a canonical uuid is still accepted");

        var rejected = false;
        try
        {
            JsonSerializer.Deserialize<Payload>("""{"id":"0OIl","assignee":null,"project":null,"labels":[]}""", options);
        }
        catch (JsonException)
        {
            rejected = true;
        }

        check(rejected, "A malformed id in a body is a JSON error, not a zero uuid");
    }

    /// <summary>Runs the constraint and the middleware in a real pipeline. The handlers below take the
    /// same parameter shapes the API's own endpoints do — a route id, an optional query id, a repeated
    /// query id and an [AsParameters] filter — because it is exactly those shapes the middleware has to
    /// recognise.</summary>
    private static async Task BindingAsync(Action<bool, string> check)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddBase58Ids();
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.AddIdConverters());

        var app = builder.Build();
        app.UseBase58Ids();

        app.MapGet("/one/{id:b58}", (Guid id) => id);
        app.MapGet("/two/{id:b58}/nested/{otherId:b58}", (Guid id, Guid otherId) => new[] { id, otherId });
        app.MapGet("/query", (Guid? teamId, Guid[]? assigneeId, string? search) =>
            new Probe(teamId, assigneeId ?? [], search));
        app.MapGet("/filter", ([Microsoft.AspNetCore.Http.AsParameters] Filter filter) =>
            new Probe(filter.TeamId, filter.LabelId ?? [], filter.Search));

        await app.StartAsync();

        try
        {
            var root = app.Urls.First();
            using var http = new HttpClient { BaseAddress = new Uri(root) };

            // The client reads what the server writes, so it needs the same converters.
            var wire = OptionalJson.CreateOptions(new Base58GuidConverter(), new NullableBase58GuidConverter());

            var id = Guid.CreateVersion7();
            var other = Guid.CreateVersion7();

            check(await http.GetFromJsonAsync<Guid>($"/one/{id.ToBase58()}", wire) == id,
                "A base58 id in the path binds to a Guid parameter");

            check(await http.GetFromJsonAsync<Guid>($"/one/{id}", wire) == id,
                "A canonical uuid in the path still binds");

            var pair = await http.GetFromJsonAsync<Guid[]>($"/two/{id.ToBase58()}/nested/{other.ToBase58()}", wire);
            check(pair is [var first, var second] && first == id && second == other,
                "Two ids in one path bind independently");

            check((await http.GetAsync($"/one/{id.ToBase58()[..21]}")).StatusCode == HttpStatusCode.NotFound,
                "A malformed id in the path is a 404 from the router");

            // A search term the same width as an id proves the rewrite is driven by the handler's
            // parameter types rather than by what a value happens to look like.
            var lookalike = other.ToBase58();
            var query = await http.GetFromJsonAsync<Probe>(
                $"/query?teamId={id.ToBase58()}&assigneeId={id.ToBase58()}&assigneeId={other.ToBase58()}&search={lookalike}",
                wire);

            check(query!.TeamId == id, "An optional id in the query string binds");
            check(query.Ids is [var a, var b] && a == id && b == other, "A repeated query id binds as a set");
            check(query.Search == lookalike, "A query string that is not an id is left alone");

            var filter = await http.GetFromJsonAsync<Probe>(
                $"/filter?teamId={id.ToBase58()}&labelId={other.ToBase58()}&search=hello", wire);

            check(filter!.TeamId == id && filter.Ids is [var only] && only == other,
                "Ids inside an [AsParameters] filter bind");

            check((await http.GetAsync($"/query?teamId={id.ToBase58()[..21]}")).StatusCode == HttpStatusCode.BadRequest,
                "A malformed id in the query string is a 400");

            var body = await http.GetStringAsync($"/one/{id.ToBase58()}");
            check(body == $"\"{id.ToBase58()}\"", "A Guid returned from a handler is written as base58");
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private sealed record Payload(Guid Id, Guid? Assignee, Optional<Guid?> Project, IReadOnlyList<Guid> Labels);

    private sealed record Probe(Guid? TeamId, IReadOnlyList<Guid> Ids, string? Search);

    private sealed record Filter(Guid? TeamId = null, Guid[]? LabelId = null, string? Search = null);
}
