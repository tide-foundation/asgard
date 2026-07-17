using Microsoft.IdentityModel.Tokens;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.OpenSsl;
using Ork.Clients.Providers;
using Tide.Asgard.Core.Crypto.Ed25519;

namespace Tide.Asgard.AspNetCore.Authentication.DPoP;

/// <summary>
///     Signs DPoP proofs with the Ed25519 key persisted in the device key PEM file, so proofs
///     and the tokens bound to them survive restarts.
///     The PKCS#8 payload is read as an RFC 8410 seed and the signing key pair is derived from
///     it per RFC 8032. Cryptide derives its key pair from the same bytes differently, so this
///     DPoP public key does not match GetDeviceKey().GetPublic() - the proof JWK must therefore
///     always come from GetPublicJwk(), never from the TideKey.
/// </summary>
public class FileDPoPKeyProvider : FileDeviceKeyProvider, IDPoPKeyProvider
{
	private readonly string _filePath;
	private SigningCredentials? Creds { get; set; }
	private EdDsaSecurityKey? _key;

	public FileDPoPKeyProvider() : this("./server.key") { }

	public FileDPoPKeyProvider(string filePath) : base(filePath)
	{
		_filePath = filePath;
	}

	// loaded on first use so the provider can be constructed before EnsureKeyExists() has run
	private EdDsaSecurityKey Key => _key ??= LoadKey(_filePath);

	public SigningCredentials GetSigningCredentials()
	{
		if (Creds != null) return Creds;
		Creds = new SigningCredentials(Key, ExtendedSecurityAlgorithms.EdDsa);
		return Creds;
	}

	public JsonWebKey GetPublicJwk()
	{
		var jwk = ExtendedJsonWebKeyConverter.ConvertFromEdDsaSecurityKey(Key);
		jwk.D = null; // public part only - this JWK ends up in every proof header
		return jwk;
	}

	private static EdDsaSecurityKey LoadKey(string filePath)
	{
		if (!File.Exists(filePath))
			throw new FileNotFoundException($"Device key file not found at {filePath}");

		using var reader = File.OpenText(filePath);
		if (new PemReader(reader).ReadObject() is not Ed25519PrivateKeyParameters privateKey)
			throw new InvalidOperationException($"The key at {filePath} is not an Ed25519 private key");

		return new EdDsaSecurityKey(EdDsa.Create(new EdDsaParameters(ExtendedSecurityAlgorithms.Curves.Ed25519)
		{
			D = privateKey.GetEncoded(),
			X = privateKey.GeneratePublicKey().GetEncoded()
		}));
	}
}
