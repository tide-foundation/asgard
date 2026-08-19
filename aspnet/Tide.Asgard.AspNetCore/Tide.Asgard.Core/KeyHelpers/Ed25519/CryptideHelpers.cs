using Cryptide.Key;
using Cryptide.Key.Schemes.P256;
using Cryptide.Signing;
using Cryptide.Tools;
using Microsoft.IdentityModel.Tokens;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Tide.Asgard.Core.Crypto.Ed25519;

namespace Tide.Asgard.Core.KeyHelpers.Ed25519;

public static class CryptideHelpers
{
	public static PublicKey ToPublicKey(this TideKey key)
	{
		return PublicKey.CreateFromSubjectPublicKeyInfo(key.ToSubjectPublicKeyInfoBytes(), out _);
	}
}
