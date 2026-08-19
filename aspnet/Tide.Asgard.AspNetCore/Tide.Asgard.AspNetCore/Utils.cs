using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using System;
using System.Collections.Generic;
using System.Text;
using Tide.Asgard.Core.Crypto.Ed25519;

namespace Tide.Asgard.AspNetCore.Authentication;

public static class Utils
{
	public const string RESOURCE_CERTIFICATE_DEFAULT_PATH = "./resource.crt";
	public const string RESOURCE_KEY_DEFAULT_PATH = "./resource.key";
	public const string RESOURCE_CERTIFICATE_REQUEST_DEFAULT_PATH = "./resource.csr";
	public const string ROOT_CA_DEFAULT_PATH = "./root.crt";
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
}
