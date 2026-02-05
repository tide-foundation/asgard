using Microsoft.IdentityModel.Tokens;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Tide.Asgard.Core.Crypto.Ed25519;

internal class ExtendedCryptoProvider : ICryptoProvider
{
	public bool IsSupportedAlgorithm(string algorithm, params object[] args)
		=> algorithm == ExtendedSecurityAlgorithms.EdDsa;

	public object Create(string algorithm, params object[] args)
	{
		if (algorithm == ExtendedSecurityAlgorithms.EdDsa && args[0] is EdDsaSecurityKey key)
		{
			return new EdDsaSignatureProvider(key, algorithm);
		}

		throw new NotSupportedException();
	}

	public void Release(object cryptoInstance)
	{
		if (cryptoInstance is IDisposable disposableObject)
			disposableObject.Dispose();
	}
}
