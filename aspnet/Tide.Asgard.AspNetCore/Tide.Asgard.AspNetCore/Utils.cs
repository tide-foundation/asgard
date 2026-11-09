using Cryptide.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Tide.Asgard.Core.Crypto.Ed25519;

namespace Tide.Asgard.AspNetCore.Authentication;

internal static class Utils
{
	public const string RESOURCE_CERTIFICATE_DEFAULT_PATH = "./resource.crt";
	public const string RESOURCE_KEY_DEFAULT_PATH = "./resource.key";
	public const string TIDE_KEY_DEFAULT_PATH = "./tide.key";
	public const string RESOURCE_CERTIFICATE_REQUEST_DEFAULT_PATH = "./resource.csr";
	public const string ROOT_CA_DEFAULT_PATH = "./root.crt";
	public const int ENROLLMENT_POLL_INTERVAL_DEFAULT_SECONDS = 60;
	public const string TIDECLOAK_JSON_ENVIRONMENT_VARIABLE = "TIDECLOAK_JSON";

	/// <summary>
	/// True when <paramref name="clientId"/> has the shape the realm CA certifies a client id in: 1 to 60 of
	/// <c>A-Z a-z 0-9 _ -</c>. The CA builds a resource certificate's subject from it, so nothing else ever appears there.
	/// </summary>
	internal static bool IsCertifiableClientId(string clientId)
		=> clientId.Length is >= 1 and <= 60 && clientId.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
	/// <summary>
	/// Finds the first jwk in the 'keys' section of the jwk set that has crv=Ed25519
	/// </summary>
	/// <param name="keycloakConfigSection"></param>
	/// <returns></returns>
	/// <exception cref="NotSupportedException"></exception>
	public static SecurityKey GetEd25519IssuerKey(this IConfiguration config) => GetEd25519IssuerKey(config.GetSection("Keycloak"));
	/// <summary>
	/// Finds the first jwk in the 'keys' section of the jwk set that has crv=Ed25519
	/// </summary>
	/// <param name="keycloakConfigSection"></param>
	/// <returns></returns>
	/// <exception cref="NotSupportedException"></exception>
	public static SecurityKey GetEd25519IssuerKey(this IConfigurationSection keycloakConfigSection)
	{
		var keysSection = keycloakConfigSection.GetSection("jwk").GetSection("keys");

		if(keysSection.Exists() == false)
			throw new InvalidOperationException("The 'keys' section is missing from the Keycloak jwk configuration.");

		foreach (var child in keysSection.GetChildren())
		{
			if (child["crv"] == "Ed25519")
			{
				keycloakConfigSection = child;
				break;
			}
		}
		if (keycloakConfigSection["crv"] != "Ed25519")
		{
			throw new NotSupportedException($"Unsupported curve type: {keycloakConfigSection["crv"]}. Only Ed25519 is supported.");
		}
		var jwk = new JsonWebKey
		{
			Kty = keycloakConfigSection["kty"],
			Crv = keycloakConfigSection["crv"],
			X = keycloakConfigSection["x"]
		};
		var signingKey = jwk.ToSecurityKey();
		return signingKey;
	}
	/// <summary>
	/// Returns the 'jti' claim from the ClaimsPrincipal, which is used as a unique identifier for the token. If the 'jti' claim is not present, an InvalidOperationException is thrown.
	/// </summary>
	/// <param name="principal"></param>
	/// <returns></returns>
	/// <exception cref="InvalidOperationException"></exception>
	public static string GetId(this ClaimsPrincipal principal)
	{
		if(principal.FindFirst("jti")?.Value is string jti && !string.IsNullOrWhiteSpace(jti))
			return jti;
		throw new InvalidOperationException("The ClaimsPrincipal does not contain a 'jti' claim.");
	}
	/// <summary>
	/// Returns a unique identifier for the token cache based on the 'jti' claim and an optional exchange target resource. If the 'jti' claim is not present, an InvalidOperationException is thrown.
	/// </summary>
	/// <param name="principal"></param>
	/// <param name="exchangeTargetResource"></param>
	/// <returns></returns>
	/// <exception cref="InvalidOperationException"></exception>
	public static string GetTokenCacheId(this ClaimsPrincipal principal, string? exchangeTargetResource = null)
	{
		if (principal.FindFirst("jti")?.Value is string jti && !string.IsNullOrWhiteSpace(jti))
			return string.IsNullOrEmpty(exchangeTargetResource) ? SHA256.HashData(jti.FromUTF8ToByteArray()).ToBase64String() : SHA256.HashData($"{jti}|{exchangeTargetResource}".FromUTF8ToByteArray()).ToBase64String();
		throw new InvalidOperationException("The ClaimsPrincipal does not contain a 'jti' claim.");
	}
}
