using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using System.Security.Cryptography;
using System.Text;

namespace Tide.Asgard.AspNetCore.Authentication.DPoP;

public interface IDPoPProofGenerator
{
	/// <summary>
	///     Creates a DPoP proof JWT (RFC 9449) for a single request.
	/// </summary>
	/// <param name="htu">The HTTP target URI of the request, without query and fragment.</param>
	/// <param name="htm">The HTTP method of the request.</param>
	/// <param name="accessToken">
	///     The access token sent alongside the proof when calling a protected resource;
	///     hashed into the 'ath' claim. Omit for token endpoint requests.
	/// </param>
	/// <param name="nonce">Server-provided nonce, when one has been issued.</param>
	string CreateProof(string htu, string htm, string? accessToken = null, string? nonce = null);
}

public class DPoPProofGenerator(IDPoPKeyProvider keyProvider) : IDPoPProofGenerator
{
	private readonly JsonWebTokenHandler _tokenHandler = new() { SetDefaultTimesOnTokenCreation = false };

	public string CreateProof(string htu, string htm, string? accessToken = null, string? nonce = null)
	{
		var claims = new Dictionary<string, object>
		{
			["jti"] = Guid.NewGuid().ToString(),
			["htm"] = htm,
			["htu"] = htu,
			["iat"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
		};

		if (accessToken != null)
			claims["ath"] = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.UTF8.GetBytes(accessToken)));

		if (nonce != null)
			claims["nonce"] = nonce;

		return _tokenHandler.CreateToken(new SecurityTokenDescriptor
		{
			TokenType = "dpop+jwt",
			SigningCredentials = keyProvider.GetSigningCredentials(),
			Claims = claims,
			AdditionalHeaderClaims = new Dictionary<string, object> { ["jwk"] = BuildPublicJwkHeaderValue() }
		});
	}

	private Dictionary<string, string> BuildPublicJwkHeaderValue()
	{
		var jwk = keyProvider.GetPublicJwk();
		if (!string.IsNullOrEmpty(jwk.D))
			throw new InvalidOperationException("The DPoP public JWK must not contain private key material.");

		// only the public members defined for each key type (RFC 7638 / RFC 8037) belong in the proof header
		var members = new Dictionary<string, string> { ["kty"] = jwk.Kty };
		if (!string.IsNullOrEmpty(jwk.Crv)) members["crv"] = jwk.Crv;
		if (!string.IsNullOrEmpty(jwk.X)) members["x"] = jwk.X;
		if (!string.IsNullOrEmpty(jwk.Y)) members["y"] = jwk.Y;
		if (!string.IsNullOrEmpty(jwk.E)) members["e"] = jwk.E;
		if (!string.IsNullOrEmpty(jwk.N)) members["n"] = jwk.N;
		return members;
	}
}
