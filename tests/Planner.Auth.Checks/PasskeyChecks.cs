using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Planner.Api.Auth;
using Planner.Domain.Identity;
using Planner.Infrastructure;

namespace Planner.Api.Checks;

/// <summary>Exercises the framework WebAuthn verifier with an actual P-256 test authenticator.
/// No database, email service, browser hardware, or mocked cryptography is required.</summary>
public static class PasskeyChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        using var ceremonies = new PasskeyCeremonies();
        var start = Context();
        ceremonies.Begin(start, "register", "server-only state", "owner");
        var finish = WithCookie(start);
        check(ceremonies.Take(finish, "register", "other-user") is null, "Registration cannot move to another account");
        check(ceremonies.Take(WithCookie(start), "register", "owner") is null, "Rejected ceremony is consumed");
        start = Context();
        ceremonies.Begin(start, "login", "state");
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => ceremonies.Take(WithCookie(start), "login"))));
        check(results.Count(x => x == "state") == 1, "Concurrent replay consumes a ceremony exactly once");
        check(ceremonies.Take(Context(), "login") is null, "A missing browser binding cannot finish sign-in");
        check(start.Response.Headers.SetCookie.ToString().Contains("httponly", StringComparison.OrdinalIgnoreCase) &&
            start.Response.Headers.SetCookie.ToString().Contains("samesite=strict", StringComparison.OrdinalIgnoreCase), "Ceremony cookie is HttpOnly and SameSite Strict");
        check(PasskeyEndpoints.IsSameOrigin(Context(), "https://planner.test") &&
            !PasskeyEndpoints.IsSameOrigin(Context(), "https://other.planner.test") &&
            !PasskeyEndpoints.IsSameOrigin(Context(), "http://planner.test") &&
            !PasskeyEndpoints.IsSameOrigin(Context(), "https://planner.test:444"), "Passkeys enforce the exact origin, including scheme and port");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthentication();
        services.AddIdentityCore<AppUser>().AddSignInManager();
        var store = new PasskeyStore();
        services.AddSingleton<IUserStore<AppUser>>(store);
        services.Configure<IdentityPasskeyOptions>(options =>
        {
            options.ResidentKeyRequirement = "required";
            options.UserVerificationRequirement = "required";
            options.ValidateOrigin = c => ValueTask.FromResult(!c.CrossOrigin && PasskeyEndpoints.IsSameOrigin(c.HttpContext, c.Origin));
        });
        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<IPasskeyHandler<AppUser>>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = store.User;
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var credentialId = RandomNumberGenerator.GetBytes(32);
        var registration = await handler.MakeCreationOptionsAsync(new PasskeyUserEntity
        {
            Id = user.Id.ToString(), Name = user.UserName!, DisplayName = user.DisplayName
        }, Context());
        using var options = JsonDocument.Parse(registration.CreationOptionsJson);
        check(options.RootElement.GetProperty("authenticatorSelection").GetProperty("residentKey").GetString() == "required",
            "Created credentials support username-free sign-in");
        var credential = Attestation(key, credentialId, Challenge(registration.CreationOptionsJson));
        var attested = await handler.PerformAttestationAsync(new()
        {
            HttpContext = Context(), AttestationState = registration.AttestationState, CredentialJson = credential
        });
        check(attested.Succeeded && attested.UserEntity.Id == user.Id.ToString(), "A real WebAuthn attestation binds the passkey to the server-selected user");
        attested.Passkey!.Name = "Test authenticator";
        check((await users.AddOrUpdatePasskeyAsync(user, attested.Passkey)).Succeeded, "Verified credential is saved through Identity");
        var request = await handler.MakeRequestOptionsAsync(null, Context());
        var assertion = Assertion(key, credentialId, user.Id, Challenge(request.RequestOptionsJson));
        async Task<PasskeyAssertionResult<AppUser>> Verify(string json, string? state = null) =>
            await handler.PerformAssertionAsync(new() { HttpContext = Context(), AssertionState = state ?? request.AssertionState, CredentialJson = json });
        var signedIn = await Verify(assertion);
        check(signedIn.Succeeded && signedIn.User.Id == user.Id, "Discoverable passkey signs in without email or password");
        check(!(await Verify(Assertion(key, credentialId, user.Id, "wrong-challenge"))).Succeeded, "Wrong challenge is rejected");
        check(!(await Verify(Assertion(key, credentialId, user.Id, Challenge(request.RequestOptionsJson), "https://attacker.test"))).Succeeded, "Foreign origin is rejected");
        check(!(await Verify(Assertion(key, credentialId, user.Id, Challenge(request.RequestOptionsJson), verified: false))).Succeeded, "User verification is required");
        using var wrongKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        check(!(await Verify(Assertion(wrongKey, credentialId, user.Id, Challenge(request.RequestOptionsJson)))).Succeeded, "Invalid signature is rejected");
        check(!(await Verify(Assertion(key, credentialId, Guid.NewGuid(), Challenge(request.RequestOptionsJson)))).Succeeded, "Wrong user handle is rejected");
        check(!(await Verify("{}")).Succeeded, "Malformed credential is rejected");
        await users.RemovePasskeyAsync(user, credentialId);
        check(!(await Verify(assertion)).Succeeded, "Removed passkey cannot sign in");

        using var db = new DesignTimeDbContextFactory().CreateDbContext([]);
        var passkeyModel = db.Model.FindEntityType(typeof(IdentityUserPasskey<Guid>));
        check(passkeyModel?.GetTableName() == "user_passkeys", "EF model includes the persistent Identity passkey store");
        check(db.Database.GenerateCreateScript().Contains("user_passkeys"), "Postgres schema generation supports JSON-owned passkey data");
    }

    private static DefaultHttpContext Context()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("planner.test");
        context.Request.Headers.Origin = "https://planner.test";
        return context;
    }
    private static DefaultHttpContext WithCookie(HttpContext from)
    {
        var context = Context();
        context.Request.Headers.Cookie = from.Response.Headers.SetCookie.ToString().Split(';')[0];
        return context;
    }
    private static string Challenge(string options) => JsonDocument.Parse(options).RootElement.GetProperty("challenge").GetString()!;
    private static string B64(byte[] bytes) => WebEncoders.Base64UrlEncode(bytes);
    private static byte[] ClientData(string type, string challenge, string origin) => JsonSerializer.SerializeToUtf8Bytes(new { type, challenge, origin, crossOrigin = false });
    private static byte[] AuthData(byte flags) => [.. SHA256.HashData(Encoding.UTF8.GetBytes("planner.test")), flags, 0, 0, 0, 0];
    private static string Attestation(ECDsa key, byte[] id, string challenge)
    {
        var parameters = key.ExportParameters(false);
        var cose = new CborWriter();
        cose.WriteStartMap(5);
        cose.WriteInt32(1); cose.WriteInt32(2);
        cose.WriteInt32(3); cose.WriteInt32(-7);
        cose.WriteInt32(-1); cose.WriteInt32(1);
        cose.WriteInt32(-2); cose.WriteByteString(parameters.Q.X!);
        cose.WriteInt32(-3); cose.WriteByteString(parameters.Q.Y!);
        cose.WriteEndMap();
        byte[] data = [.. AuthData(0x45), .. new byte[16], 0, (byte)id.Length, .. id, .. cose.Encode()];
        var attestation = new CborWriter();
        attestation.WriteStartMap(3);
        attestation.WriteTextString("fmt"); attestation.WriteTextString("none");
        attestation.WriteTextString("attStmt"); attestation.WriteStartMap(0); attestation.WriteEndMap();
        attestation.WriteTextString("authData"); attestation.WriteByteString(data);
        attestation.WriteEndMap();
        return JsonSerializer.Serialize(new
        {
            id = B64(id), rawId = B64(id), type = "public-key", clientExtensionResults = new { },
            response = new { clientDataJSON = B64(ClientData("webauthn.create", challenge, "https://planner.test")), attestationObject = B64(attestation.Encode()), transports = new[] { "internal" } }
        });
    }
    private static string Assertion(ECDsa key, byte[] id, Guid userId, string challenge, string origin = "https://planner.test", bool verified = true)
    {
        var data = AuthData(verified ? (byte)5 : (byte)1);
        var clientData = ClientData("webauthn.get", challenge, origin);
        var signed = key.SignData([.. data, .. SHA256.HashData(clientData)], HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return JsonSerializer.Serialize(new
        {
            id = B64(id), rawId = B64(id), type = "public-key", clientExtensionResults = new { },
            response = new { clientDataJSON = B64(clientData), authenticatorData = B64(data), signature = B64(signed), userHandle = B64(Encoding.UTF8.GetBytes(userId.ToString())) }
        });
    }

    private sealed class PasskeyStore : IUserStore<AppUser>, IUserPasskeyStore<AppUser>
    {
        public AppUser User { get; } = new() { Id = Guid.NewGuid(), UserName = "test@planner.test", DisplayName = "Test" };
        private readonly Dictionary<string, UserPasskeyInfo> keys = new();
        public Task AddOrUpdatePasskeyAsync(AppUser user, UserPasskeyInfo key, CancellationToken ct) { keys[B64(key.CredentialId)] = key; return Task.CompletedTask; }
        public Task<IList<UserPasskeyInfo>> GetPasskeysAsync(AppUser user, CancellationToken ct) => Task.FromResult<IList<UserPasskeyInfo>>(keys.Values.ToList());
        public Task<AppUser?> FindByPasskeyIdAsync(byte[] id, CancellationToken ct) => Task.FromResult(keys.ContainsKey(B64(id)) ? User : null);
        public Task<UserPasskeyInfo?> FindPasskeyAsync(AppUser user, byte[] id, CancellationToken ct) => Task.FromResult(user.Id == User.Id ? keys.GetValueOrDefault(B64(id)) : null);
        public Task RemovePasskeyAsync(AppUser user, byte[] id, CancellationToken ct) { keys.Remove(B64(id)); return Task.CompletedTask; }
        public Task<string> GetUserIdAsync(AppUser user, CancellationToken ct) => Task.FromResult(user.Id.ToString());
        public Task<string?> GetUserNameAsync(AppUser user, CancellationToken ct) => Task.FromResult(user.UserName);
        public Task SetUserNameAsync(AppUser user, string? name, CancellationToken ct) { user.UserName = name; return Task.CompletedTask; }
        public Task<string?> GetNormalizedUserNameAsync(AppUser user, CancellationToken ct) => Task.FromResult(user.NormalizedUserName);
        public Task SetNormalizedUserNameAsync(AppUser user, string? name, CancellationToken ct) { user.NormalizedUserName = name; return Task.CompletedTask; }
        public Task<IdentityResult> CreateAsync(AppUser user, CancellationToken ct) => Task.FromResult(IdentityResult.Success);
        public Task<IdentityResult> UpdateAsync(AppUser user, CancellationToken ct) => Task.FromResult(IdentityResult.Success);
        public Task<IdentityResult> DeleteAsync(AppUser user, CancellationToken ct) => Task.FromResult(IdentityResult.Success);
        public Task<AppUser?> FindByIdAsync(string id, CancellationToken ct) => Task.FromResult(id == User.Id.ToString() ? User : null);
        public Task<AppUser?> FindByNameAsync(string name, CancellationToken ct) => Task.FromResult<AppUser?>(User);
        public void Dispose() { }
    }
}
