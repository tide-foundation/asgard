using Microsoft.IdentityModel.Tokens;

namespace Tide.Asgard.AspNetCore.Authentication.DPoP;

/// <summary>
///     Supplies the key pair used to sign DPoP proofs (RFC 9449) for outgoing requests to Tidecloak.
/// </summary>
public interface IDPoPKeyProvider
{
	/// <summary>
	///     Signing credentials wrapping the client's DPoP private key.
	/// </summary>
	SigningCredentials GetSigningCredentials();

	/// <summary>
	///     The public JWK embedded in the header of every DPoP proof. Must not contain private key material.
	/// </summary>
	JsonWebKey GetPublicJwk();
}
