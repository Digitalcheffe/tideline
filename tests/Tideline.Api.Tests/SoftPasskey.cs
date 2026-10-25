using System.Buffers.Text;
using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Tideline.Api.Tests;

/// <summary>
/// A passkey made of software, for tests: it does what a phone or a security key does for WebAuthn (a P-256 key, "none" attestation, signed
/// assertions), so the server's checks run against real bytes. <paramref name="origin"/> is where the "browser" says it is.
/// </summary>
public sealed class SoftPasskey(string origin, byte[]? credentialId = null)
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private uint _counter;

    public byte[] CredentialId { get; } = credentialId ?? RandomNumberGenerator.GetBytes(32);

    public static string B64(byte[] bytes) => Base64Url.EncodeToString(bytes);

    private static byte[] Unb64(string text) => Base64Url.DecodeFromChars(text);

    private byte[] ClientData(string type, string challenge, string? originOverride) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type, challenge, origin = originOverride ?? origin, crossOrigin = false }));

    private byte[] CoseKey()
    {
        var p = _key.ExportParameters(false);
        var w = new CborWriter(CborConformanceMode.Ctap2Canonical);
        w.WriteStartMap(5);
        w.WriteInt32(1); w.WriteInt32(2);        // key type: EC2
        w.WriteInt32(3); w.WriteInt32(-7);       // algorithm: ES256
        w.WriteInt32(-1); w.WriteInt32(1);       // curve: P-256
        w.WriteInt32(-2); w.WriteByteString(p.Q.X!);
        w.WriteInt32(-3); w.WriteByteString(p.Q.Y!);
        w.WriteEndMap();
        return w.Encode();
    }

    private static byte[] Be(uint n) => [(byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n];

    /// <summary>What <c>navigator.credentials.create</c> hands back, for the options the server sent.</summary>
    public object Create(JsonElement options, string? rpId = null, string? originOverride = null)
    {
        var challenge = options.GetProperty("challenge").GetString()!;
        var rp = rpId ?? options.GetProperty("rp").GetProperty("id").GetString()!;

        var authData = SHA256.HashData(Encoding.UTF8.GetBytes(rp))
            .Concat(new byte[] { 0x45 })           // user present, user verified, credential data included
            .Concat(Be(_counter))
            .Concat(new byte[16])                  // no AAGUID
            .Concat(new[] { (byte)(CredentialId.Length >> 8), (byte)CredentialId.Length })
            .Concat(CredentialId)
            .Concat(CoseKey()).ToArray();

        var attestation = new CborWriter(CborConformanceMode.Ctap2Canonical);
        attestation.WriteStartMap(3);
        attestation.WriteTextString("fmt"); attestation.WriteTextString("none");
        attestation.WriteTextString("attStmt"); attestation.WriteStartMap(0); attestation.WriteEndMap();
        attestation.WriteTextString("authData"); attestation.WriteByteString(authData);
        attestation.WriteEndMap();

        return new
        {
            id = B64(CredentialId), rawId = B64(CredentialId), type = "public-key",
            response = new { attestationObject = B64(attestation.Encode()), clientDataJSON = B64(ClientData("webauthn.create", challenge, originOverride)) },
            clientExtensionResults = new { },
        };
    }

    /// <summary>What <c>navigator.credentials.get</c> hands back, for the options the server sent.</summary>
    public object Assert(JsonElement options, string? originOverride = null, bool verified = true)
    {
        var challenge = options.GetProperty("challenge").GetString()!;
        var rp = options.GetProperty("rpId").GetString()!;
        _counter++;

        var authData = SHA256.HashData(Encoding.UTF8.GetBytes(rp)).Concat(new byte[] { (byte)(verified ? 0x05 : 0x01) }).Concat(Be(_counter)).ToArray();
        var clientData = ClientData("webauthn.get", challenge, originOverride);
        var signature = _key.SignData(authData.Concat(SHA256.HashData(clientData)).ToArray(), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        return new
        {
            id = B64(CredentialId), rawId = B64(CredentialId), type = "public-key",
            response = new { authenticatorData = B64(authData), clientDataJSON = B64(clientData), signature = B64(signature), userHandle = (string?)null },
            clientExtensionResults = new { },
        };
    }
}
