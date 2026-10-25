using System.Buffers.Text;
using System.Collections.Concurrent;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Tideline.Data.Security;

namespace Tideline.Api.Auth;

/// <summary>
/// The WebAuthn side of passkeys (docs/09-security.md): making the challenge, and checking what the person's device sends back with Fido2NetLib.
/// Private keys never reach the server; only the public key and the credential id are kept. User verification (a fingerprint, a face or a
/// PIN on the device) is required, and a challenge works once and for five minutes.
/// </summary>
public sealed class PasskeyService(TidelineOptions options, TimeProvider clock)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private sealed record Pending(string Purpose, object Options, DateTimeOffset Expires);

    private readonly ConcurrentDictionary<long, Pending> _pending = new();

    private readonly Lazy<Fido2> _fido2 = new(() =>
    {
        var origin = new Uri(options.PublicUrl);
        var origins = new HashSet<string> { origin.GetLeftPart(UriPartial.Authority) };
        return new Fido2(new Fido2Configuration { RPID = origin.Host, RPName = "Tideline", Origins = origins });
    });

    /// <summary>The address people reach the app on is what a passkey is tied to: a passkey made for one address does not work on another.</summary>
    public string RelyingPartyId => new Uri(options.PublicUrl).Host;

    private void Remember(long sessionId, string purpose, object value)
    {
        var now = clock.GetUtcNow();
        foreach (var stale in _pending.Where(p => p.Value.Expires <= now).Select(p => p.Key).ToList()) _pending.TryRemove(stale, out _);
        _pending[sessionId] = new Pending(purpose, value, now + Lifetime);
    }

    private T Take<T>(long sessionId, string purpose) where T : class
    {
        if (!_pending.TryRemove(sessionId, out var p) || p.Purpose != purpose || p.Expires <= clock.GetUtcNow() || p.Options is not T value)
            throw new AuthException("That took too long or was already used. Start again.", 409);
        return value;
    }

    private static byte[] Decode(string base64Url) => Base64Url.DecodeFromChars(base64Url);

    private static string Encode(byte[] bytes) => Base64Url.EncodeToString(bytes);

    // ---- adding a passkey ---------------------------------------------------------------------------------------

    public CredentialCreateOptions BeginEnrollment(long sessionId, UserRecord user, IReadOnlyList<MfaMethod> existing)
    {
        var who = new Fido2User { Id = BitConverter.GetBytes(user.Id), Name = user.Email, DisplayName = user.DisplayName };
        var exclude = existing.Where(m => m.Kind == "passkey" && m.CredentialId is not null).Select(m => new PublicKeyCredentialDescriptor(Decode(m.CredentialId!))).ToList();

        var created = _fido2.Value.RequestNewCredential(new RequestNewCredentialParams
        {
            User = who,
            ExcludeCredentials = exclude,
            AuthenticatorSelection = new AuthenticatorSelection { ResidentKey = ResidentKeyRequirement.Preferred, UserVerification = UserVerificationRequirement.Required },
            AttestationPreference = AttestationConveyancePreference.None,
        });
        Remember(sessionId, "enroll", created);
        return created;
    }

    /// <returns>The credential id and the public key (both text), and the counter the device started at.</returns>
    public (string CredentialId, string PublicKey, long SignCount) CompleteEnrollment(long sessionId, AuthenticatorAttestationRawResponse response)
    {
        var created = Take<CredentialCreateOptions>(sessionId, "enroll");
        try
        {
            var result = _fido2.Value.MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = response,
                OriginalOptions = created,
                IsCredentialIdUniqueToUserCallback = (_, _) => Task.FromResult(true), // the unique index on credential_id has the last word
            }).GetAwaiter().GetResult();
            return (Encode(result.Id), Convert.ToBase64String(result.PublicKey), result.SignCount);
        }
        catch (Fido2VerificationException)
        {
            throw new AuthException("The passkey could not be checked. Try again, or use an authenticator app instead.", 400);
        }
    }

    // ---- signing in with one ------------------------------------------------------------------------------------

    public AssertionOptions BeginAssertion(long sessionId, IReadOnlyList<MfaMethod> passkeys)
    {
        var allowed = passkeys.Where(m => m.CredentialId is not null).Select(m => new PublicKeyCredentialDescriptor(Decode(m.CredentialId!))).ToList();
        var asked = _fido2.Value.GetAssertionOptions(new GetAssertionOptionsParams { AllowedCredentials = allowed, UserVerification = UserVerificationRequirement.Required });
        Remember(sessionId, "assert", asked);
        return asked;
    }

    /// <summary>The passkey that signed and the counter it reported, or null if the answer is not good. The challenge is used up either way.</summary>
    public (MfaMethod Method, long SignCount)? CompleteAssertion(long sessionId, AuthenticatorAssertionRawResponse response, IReadOnlyList<MfaMethod> passkeys)
    {
        var asked = Take<AssertionOptions>(sessionId, "assert");
        var credentialId = Encode(response.RawId);
        var method = passkeys.FirstOrDefault(m => m.CredentialId == credentialId);
        if (method is null) return null;

        try
        {
            var result = _fido2.Value.MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = response,
                OriginalOptions = asked,
                StoredPublicKey = Convert.FromBase64String(method.PublicKey!),
                StoredSignatureCounter = (uint)method.SignCount,
                IsUserHandleOwnerOfCredentialIdCallback = (_, _) => Task.FromResult(true), // only this person's own passkeys were offered
            }).GetAwaiter().GetResult();
            return (method, result.SignCount);
        }
        catch (Fido2VerificationException)
        {
            return null;
        }
    }
}
