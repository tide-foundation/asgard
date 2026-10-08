using Cryptide;
using Cryptide.Key;
using Cryptide.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
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
	Task<TokenExchangeResponse> ExchangeToken();
	Task<TokenExchangeResponse> ExchangeToken(string requestingClientId, string? requestedAudience = null);
}
public record TokenExchangeResponse(string ApplicationAccessToken, string? ApplicationDoken, DateTime ExpiresAt);
public class TokenExchangeService(IHttpClientFactory factory, IHttpContextAccessor httpContextAccessor, IConfiguration config, IAsgardCache cache) : ITokenExchangeService
{
	private HttpContext GetHttpContext() => httpContextAccessor.HttpContext ?? throw new InvalidOperationException($"HTTP context is not available. Ensure {nameof(TokenExchangeService)} is only used in Controllers");
	public async Task<TokenExchangeResponse> ExchangeToken()
	{
		var requestingClientId = config.GetSection("Keycloak")["resource"] ?? throw new InvalidOperationException("Keycloak resource is not configured in appsettings.json");
		return await ExchangeToken(requestingClientId);
	}
	public async Task<TokenExchangeResponse> ExchangeToken(string requestingClientId, string? requestedAudience = null)
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
			throw new InvalidOperationException("Only DPoP token exchange is supported");
	}

	/// <summary>
	/// Looks at the DPoP Header
	/// </summary>
	/// <returns></returns>
	private async Task<TokenExchangeResponse> ExchangeDPoPToken(string requestingClientId, string? requestedAudience = null)
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

		string? approverDelegationSignature = null;
		if (context.Items.TryGetValue("ValidatedTideDelegationSignature", out var tideEnclaveApprovalItem))
		{
			approverDelegationSignature = tideEnclaveApprovalItem!.ToString() ?? throw new InvalidOperationException("Invalid tide delegation signature.");
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
		if (approverDelegationSignature != null) forms["tide_delegation"] = approverDelegationSignature;
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

		string accessToken = token.GetString() ?? throw new InvalidOperationException("Token exchange response contained a null access_token.");

		Console.WriteLine($"Token exchange response contained an access_token: {accessToken}");

		if (!result.TryGetProperty("expires_in", out var expiresIn))
			throw new InvalidOperationException("Token exchange response did not contain an expires_in.");

		var expiresAt = DateTime.UtcNow.AddSeconds(expiresIn.GetInt32());

		// add tokens to cache
		var tokenId = context.User.GetId();

		await cache.AddApplicationToken(tokenId, accessToken, expiresAt);

		string? applicationDoken = null;
		if (result.TryGetProperty("doken", out var doken))
		{
			Console.WriteLine($"Token exchange response contained a doken: {doken.GetString()}");
			// add doken to cache
			applicationDoken = doken.GetString()!;
			await cache.AddApplicationTideDoken(tokenId, applicationDoken, expiresAt);
		}

		return new TokenExchangeResponse(accessToken, applicationDoken, expiresAt);
	}
}