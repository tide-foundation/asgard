using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.JsonWebTokens;
using Tide.Asgard.AspNetCore.Authentication.TokenExchange;
using Tide.Asgard.AspNetCore.DPoP.Exchange;

namespace Asgard.TestServer.Controllers;

/// <summary>
/// Two endpoints, no business logic.
///
/// GET /Hello          — [Authorize] + globally-required DPoP. A 200 proves the
///                       user authenticated and the token was DPoP-bound.
/// GET /Hello/exchange — additionally requires DPoP resource delegation, then
///                       exchanges the user's token. A 200 proves mTLS worked.
/// </summary>
[Authorize]
[ApiController]
[Route("[controller]")]
public class HelloController(
	IConfiguration config,
	ITokenExchangeService tokenExchangeService) : ControllerBase
{
	[HttpGet]
	public IActionResult Get() => Ok(new
	{
		message = "hi!",
		user = User.FindFirst("preferred_username")?.Value,
		// Present only on a DPoP-bound token; the thumbprint of the proof key.
		cnfJkt = User.FindFirst("cnf")?.Value,
	});

	/// <summary>
	/// Token exchange, which is also the mTLS test.
	///
	/// The app exchanges the caller's token for one issued to the confidential
	/// client it authenticates AS ("backend"). That client is configured with
	/// clientAuthenticatorType=tide-mtls, so TideCloak will only honour the
	/// exchange if the app presented a valid enrolled client certificate — a 200
	/// here is therefore proof that mTLS authentication succeeded, and a failure
	/// before the certificate is issued is expected rather than surprising.
	///
	/// [RequireDPoPExchangeApproval] is not optional: for a DPoP-authenticated
	/// request ExchangeToken reads the validated delegation proof this filter
	/// puts in HttpContext.Items, and throws DPoPDelegationProofNotFound without
	/// it. The filter challenges with DPoP-Delegation-Key / -Challenge, which the
	/// client answers with a DPoP-Resource-Delegation header.
	/// </summary>
	[HttpGet("exchange")]
	[RequireDPoPExchangeApproval]
	public async Task<IActionResult> Exchange()
	{
		var requestingClientId = config.GetSection("Keycloak")["resource"]!;
		var exchanged = await tokenExchangeService.ExchangeToken(requestingClientId);

		var jwt = new JsonWebToken(exchanged);
		jwt.TryGetPayloadValue<string>("azp", out var azp);
		jwt.TryGetPayloadValue<string>("preferred_username", out var username);

		// NOTE: `token` is the real exchanged credential. It is returned so the
		// test can print it, which is only acceptable because this server exists
		// solely for tests and the realm it belongs to is torn down at the end of
		// the run. Do NOT copy this into a real resource.
		return Ok(new
		{
			exchanged = true,
			requestingClientId,
			azp,
			aud = jwt.Audiences,
			sub = jwt.Subject,
			iss = jwt.Issuer,
			user = username,
			token = exchanged,
		});
	}
}
