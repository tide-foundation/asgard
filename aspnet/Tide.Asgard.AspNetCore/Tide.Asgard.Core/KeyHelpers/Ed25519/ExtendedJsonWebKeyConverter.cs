using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Tide.Asgard.Core.Crypto.Ed25519;

public static class ExtendedJsonWebKeyConverter
{
	public static JsonWebKey ConvertFromEdDsaSecurityKey(EdDsaSecurityKey key)
	{

		var parameters = key.EdDsa.Parameters;
		return new JsonWebKey
		{
			Crv = parameters.Curve,
			X = parameters.X != null ? Base64UrlEncoder.Encode(parameters.X) : null,
			D = parameters.D != null ? Base64UrlEncoder.Encode(parameters.D) : null,
			Kty = ExtendedSecurityAlgorithms.KeyTypes.Ecdh,
			Alg = ExtendedSecurityAlgorithms.EdDsa,
			CryptoProviderFactory = key.CryptoProviderFactory,
		};
	}
	public static SecurityKey ToSecurityKey(this JsonWebKey webKey)
	{
		ArgumentNullException.ThrowIfNull(webKey);
		if(TryConvertToEdDsaSecurityKey(webKey, out var edDsaSecurityKey))
		{
			return edDsaSecurityKey;
		}
		return webKey;
	}
	public static byte[] ComputeJwkThumbprint(JsonWebKey webKey)
	{
		ArgumentNullException.ThrowIfNull(webKey);

		if (webKey.Kty == ExtendedSecurityAlgorithms.KeyTypes.Ecdh)
		{
			// RFC 7638 § 3.2 + RFC 8037 § 2: required members for OKP in lexicographic order
			var json = $"{{\"crv\":\"{webKey.Crv}\",\"kty\":\"{webKey.Kty}\",\"x\":\"{webKey.X}\"}}";
			return SHA256.HashData(Encoding.UTF8.GetBytes(json));
		}

		return webKey.ComputeJwkThumbprint();
	}

	public static bool TryConvertToEdDsaSecurityKey(JsonWebKey webKey, out EdDsaSecurityKey key)
	{
		key = null;

		if (webKey != null && webKey.Kty == ExtendedSecurityAlgorithms.KeyTypes.Ecdh)
		{
			if (webKey.Crv == ExtendedSecurityAlgorithms.Curves.Ed25519
				|| webKey.Crv == ExtendedSecurityAlgorithms.Curves.Ed448)
			{
				try
				{
					key = new EdDsaSecurityKey(EdDsa.Create(new EdDsaParameters(webKey.Crv)
					{
						X = webKey.X != null ? Base64UrlEncoder.DecodeBytes(webKey.X) : null,
						D = webKey.D != null ? Base64UrlEncoder.DecodeBytes(webKey.D) : null
					}));

					return true;
				}
				catch (Exception ex)
				{
					LogHelper.LogWarning(LogHelper.FormatInvariant("Unable to create an EdDsaSecurityKey from the properties found in the JsonWebKey: '{0}', Exception '{1}'.", webKey, ex));
				}

			}
		}

		return false;
	}
}
