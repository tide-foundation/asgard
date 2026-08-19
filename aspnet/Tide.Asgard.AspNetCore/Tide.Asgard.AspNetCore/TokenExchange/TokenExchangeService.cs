using Cryptide.Key;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.VisualBasic;
using Ork.Clients.Providers;
using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Tide.Asgard.Core;
using Tide.Asgard.Core.Crypto.Ed25519;
using Tide.Asgard.Core.PolicyHelpers;

namespace Tide.Asgard.AspNetCore.Authentication.TokenExchange;

public enum ResourceAuthenticationMode
{
	/// <summary>
	/// Asgard will look for a mTLS key if available. If none found, will attempt to enroll a mTLS resource certificate using enrollment token, resulting in a Tide Resource Identity.
	/// </summary>
	AutoMTLSEnrollment,
	/// <summary>
	/// Constant mTLS authentication to Tidecloak. Your resource will ONLY authenticate with the mTLS credentials.
	/// </summary>
	MTLS,

}
public interface ITokenExchangeService
{
	Task<string> ExchangeToken(string requestingClientId, string? requestedAudience = null);
	Task<string> ExchangeTideDokenForApplicationDoken();
}
public class TokenExchangeService(IHttpClientFactory factory, IHttpContextAccessor httpContextAccessor, IResourceKeyProvider resourceKeyProvider) : ITokenExchangeService
{
	private HttpContext GetHttpContext() => httpContextAccessor.HttpContext ?? throw new InvalidOperationException($"HTTP context is not available. Ensure {nameof(TokenExchangeService)} is only used in Controllers");
	public async Task<string> ExchangeToken(string requestingClientId, string? requestedAudience = null)
	{
		var context = GetHttpContext();
		if (context.User.Identity?.IsAuthenticated != true)
		{
			throw new InvalidOperationException($"{nameof(TokenExchangeService)} can only be used in authenticated contexts.");
		}
		// Determine if its an access token or dpop token
		//   - DPoP tokens will have both an "Authorization: DPoP ..." header and a "DPoP" proof header
		//   - Bearer tokens will only have an "Authorization: Bearer ..." header
		//   - ignore dokens as the user will never manage those themselves -> this SDK will
		var isDPoP = context.Request.Headers.ContainsKey("DPoP") &&
					 context.Request.Headers.TryGetValue("Authorization", out var auth) &&
					 auth.ToString().StartsWith("DPoP ", StringComparison.OrdinalIgnoreCase);

		if (isDPoP)
			return await ExchangeDPoPToken(requestingClientId, requestedAudience);
		else
			return await ExchangeAccessToken(requestingClientId, requestedAudience);
	}

	/// <summary>
	/// Looks at the Bearer Header
	/// </summary>
	/// <returns></returns>
	private async Task<string> ExchangeAccessToken(string requestingClientId, string? requestedAudience = null)
	{
		if (!GetHttpContext().Request.Headers.TryGetValue("Authorization", out var authHeader))
			throw new UnauthorizedAccessException("Authorization header is missing.");

		var headerValue = authHeader.ToString();

		if (!headerValue.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
			throw new UnauthorizedAccessException("Authorization header is not a Bearer token.");

		var userAccessToken = headerValue["Bearer ".Length..].Trim();

		// No exchange proof validation required
		// We can continue directly to an exchange

		var client = factory.CreateClient("Tidecloak");

		var forms = new Dictionary<string, string>
		{
			["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
			["client_id"] = requestingClientId,
			["subject_token"] = userAccessToken,
			["subject_token_type"] = "urn:ietf:params:oauth:token-type:access_token",
		};
		if(requestedAudience != null) forms["audience"] = requestedAudience;
		var body = new FormUrlEncodedContent(forms);

		var resp = await client.PostAsync(
			$"protocol/openid-connect/token", body);

		if (!resp.IsSuccessStatusCode)
		{
			var error = await resp.Content.ReadAsStringAsync();
			throw new HttpRequestException(
				$"Token exchange failed: {resp.StatusCode} - {error}");
		}

		var json = await resp.Content.ReadAsStringAsync();
		var result = JsonDocument.Parse(json).RootElement;

		if (!result.TryGetProperty("access_token", out var token))
			throw new InvalidOperationException("Token exchange response did not contain an access_token.");

		return token.GetString()!;
	}

	/// <summary>
	/// Looks at the DPoP Header
	/// </summary>
	/// <returns></returns>
	private async Task<string> ExchangeDPoPToken(string requestingClientId, string? requestedAudience = null)
	{
		var context = GetHttpContext();
		if(!context.Items.TryGetValue("ValidatedDPoPResourceDelegationProof", out var dpopProofItem))
		{
			throw new AsgardException(AsgardErrorCode.DPoPDelegationProofNotFound);
		}
		var dpopProof = dpopProofItem as string ?? throw new AsgardException(AsgardErrorCode.DPoPDelegationProofNotFound);

		if (!context.Request.Headers.TryGetValue("Authorization", out var authHeader))
			throw new UnauthorizedAccessException("Authorization header is missing.");
		if (!context.Request.Headers.TryGetValue("DPoP", out var dpopProofHeader))
			throw new UnauthorizedAccessException("DPoP header is missing.");

		var headerValue = authHeader.ToString();

		if (!headerValue.StartsWith("DPoP ", StringComparison.OrdinalIgnoreCase))
			throw new UnauthorizedAccessException("Authorization header is not a DPoP token.");

		var userDPoPToken = headerValue["DPoP ".Length..].Trim();

		var client = factory.CreateClient("Tidecloak");

		if(client.DefaultRequestHeaders.Authorization == null && context.Items.TryGetValue("ValidatedSessionKeyApproval", out var sessionKeyApprovalItem))
		{
			// Means we are using mTLS to communicate with Tidecloak (Client-Secret or Signed JWT require Authorization headers)
			// AND the user provided a SessionKeyApproval -> we need to create an ephemeral EdDSA key to tie the resulting doken to.

			// Generate EdDSA key
			var ephemeralEdDSAKey = TideKey.NewKey();

			// Approve EdDSA key with current P-256 resource key
			var currentResourceKey = resourceKeyProvider.GetResourceKey();

			// how can i get the current resource key? DeviceKeyProvider?

			// Add approval to actor_token param
		}

		var forms = new Dictionary<string, string>
		{
			["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
			["client_id"] = requestingClientId,
			["subject_token"] = userDPoPToken,
			["subject_token_type"] = "urn:ietf:params:oauth:token-type:access_token",
			["actor_token"] = dpopProof,
			["actor_token_type"] = "urn:ietf:params:oauth:token-type:delegation+jwt"
		};
		if (requestedAudience != null) forms["audience"] = requestedAudience;
		var body = new FormUrlEncodedContent(forms);

		var resp = await client.PostAsync(
			$"protocol/openid-connect/token", body);

		if (!resp.IsSuccessStatusCode)
		{
			var error = await resp.Content.ReadAsStringAsync();
			throw new HttpRequestException(
				$"Token exchange failed: {resp.StatusCode} - {error}");
		}

		var json = await resp.Content.ReadAsStringAsync();
		var result = JsonDocument.Parse(json).RootElement;

		if (!result.TryGetProperty("access_token", out var token))
			throw new InvalidOperationException("Token exchange response did not contain an access_token.");

		Console.WriteLine(token.GetString()!);

		return token.GetString()!;
	}

	/// <summary>
	/// Looks at the Doken Header
	/// </summary>
	/// <returns></returns>
	public async Task<string> ExchangeTideDokenForApplicationDoken()
	{
		var context = GetHttpContext();

		var headers = context.Request.Headers;
		if(headers == null || !headers.TryGetValue("Application-Doken", out var applicationDoken) || !headers.TryGetValue("User-Doken", out var userDoken))
		{
			throw new AsgardException(AsgardErrorCode.DokenNotFound, headers =>
			{
				headers["Delegation-Key"] = Base64UrlEncoder.Encode(resourceKeyProvider.GetResourceKey().GetPublic().ToJwk()); // maybe it's better to return a standard serialized (not SerializedComponent)
			});
		}


		//var d = deviceKeyProvider.GetDeviceKeyAsString(); // WE NEED THIS DON'T REMOVE

		// WE NEED IT SO WE HAVE ACCESS TO THE DEVICE KEY TO VALIDATE THE DOKEN EXCHANGE PROOF

		// This

		





		throw new NotImplementedException();

		if (!GetHttpContext().Request.Headers.TryGetValue("Authorization", out var authHeader))
			throw new UnauthorizedAccessException("Authorization header is missing.");
		if (!GetHttpContext().Request.Headers.TryGetValue("Doken-Exchange-Proof", out var dokenExProof))
			throw new UnauthorizedAccessException("Doken Exchange Proof header is missing.");

		var headerValue = authHeader.ToString();
		var exchangeProof = dokenExProof.ToString();

		if (!headerValue.StartsWith("Doken ", StringComparison.OrdinalIgnoreCase))
			throw new UnauthorizedAccessException("Authorization header is not a Tide Doken.");

		//var userDoken = headerValue["Doken ".Length..].Trim();



	}

	private void ValidateExchangeProof(SecurityKey userBoundKey, JwtSecurityToken exchangeProof)
	{
		// Ensure that the session key in the token / doken signed this client's certificate

		throw new NotImplementedException();
	}
}
internal sealed class TokenExchangeClientMarker { }
