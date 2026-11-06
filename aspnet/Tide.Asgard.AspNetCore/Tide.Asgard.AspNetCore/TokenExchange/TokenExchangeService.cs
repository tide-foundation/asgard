using Cryptide;
using Cryptide.Key;
using Cryptide.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Ork.Clients.Providers;
using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Tide.Asgard.AspNetCore.DPoP;
using Tide.Asgard.Core;
using Tide.Asgard.Core.Crypto.Ed25519;
using Tide.Asgard.Core.PolicyHelpers;

namespace Tide.Asgard.AspNetCore.Authentication.TokenExchange;

internal record TokenExchangeOptions(string subjectToken, string? scope = null, string? audience = null);
public interface ITokenExchangeService
{
	Task<TokenExchangeResponse> ExchangeToken();
	Task<TokenExchangeResponse> ExchangeToken(string requestingClientId, string? requestedAudience = null);
	//Task<TokenExchangeResponse> ExchangeToken(TokenExchangeOptions options);
}
public record TokenExchangeResponse(string ApplicationAccessToken, string? ApplicationDoken, DateTime ExpiresAt);
internal class TokenExchangeService(IHttpClientFactory factory, IHttpContextAccessor httpContextAccessor, IConfiguration config, IAsgardCache cache) : ITokenExchangeService
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

		if(!context.Request.Headers.TryGetValue("Authorization", out var auth))
			throw new InvalidOperationException("Authorization header is missing.");

		string token;
		if (auth.ToString().StartsWith("DPoP ", StringComparison.OrdinalIgnoreCase))
		{
			// ensure token has cnf.jkt claim, IN CASE this service was called without a jkt binding filter
			if(!HasConfirmation(context.User, Constants.DPoP.JwkThumbprint))
				throw new InvalidOperationException("DPoP token must contain cnf.jkt claim for token exchange.");

			token = auth.ToString().Substring("DPoP ".Length).Trim();
		}
		else if (auth.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
		{
			// ensure token has cnf.x5t#S256 claim, IN CASE this service was called without a mtls binding filter
			if(!HasConfirmation(context.User, "x5t#S256"))
				throw new InvalidOperationException("Bearer token must contain cnf.x5t#S256 claim for token exchange.");

			token = auth.ToString().Substring("Bearer ".Length).Trim();
		}
		else
			throw new InvalidOperationException("Authorization header must be either DPoP or Bearer.");

		return await ExchangeToken(token, requestingClientId, requestedAudience);
	}

	// cnf arrives as a single JSON claim, e.g. {"jkt":"...","kc-jkt-type":"DPoP"}
	private static bool HasConfirmation(ClaimsPrincipal user, string thumbprintName)
	{
		var cnf = user.FindFirst(Constants.DPoP.Cnf)?.Value;
		if (string.IsNullOrEmpty(cnf)) return false;
		try
		{
			using var document = JsonDocument.Parse(cnf);
			return document.RootElement.ValueKind == JsonValueKind.Object
				&& document.RootElement.TryGetProperty(thumbprintName, out var thumbprint)
				&& thumbprint.ValueKind == JsonValueKind.String
				&& !string.IsNullOrEmpty(thumbprint.GetString());
		}
		catch (JsonException)
		{
			return false;
		}
	}

	/// <summary>
	/// Looks at the DPoP Header
	/// </summary>
	/// <returns></returns>
	private async Task<TokenExchangeResponse> ExchangeToken(string subjectToken, string requestingClientId, string? requestedAudience = null)
	{
		var context = GetHttpContext();
		if(!context.Items.TryGetValue("ValidatedResourceDelegationProof", out var delegationProofItem))
		{
			throw new AsgardException(AsgardErrorCode.DelegationProofNotFound);
		}
		var delegationProof = delegationProofItem as string ?? throw new AsgardException(AsgardErrorCode.DelegationProofNotFound);

		string? approverDelegationSignature = null;
		if (context.Items.TryGetValue("ValidatedTideDelegationSignature", out var tideEnclaveApprovalItem))
		{
			approverDelegationSignature = tideEnclaveApprovalItem!.ToString() ?? throw new InvalidOperationException("Invalid tide delegation signature.");
		}

		var forms = new Dictionary<string, string>
		{
			["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
			["client_id"] = requestingClientId,
			["subject_token"] = subjectToken,
			["subject_token_type"] = "urn:ietf:params:oauth:token-type:access_token",
			["actor_token"] = delegationProof,
			["actor_token_type"] = "urn:ietf:params:oauth:token-type:delegation+jwt"
		};
		if (approverDelegationSignature != null) forms["tide_delegation"] = approverDelegationSignature;
		if (requestedAudience != null) forms["audience"] = requestedAudience;
		var body = new FormUrlEncodedContent(forms);

		return await ExecuteExchange(context, body, requestedAudience);
	}
	private async Task<TokenExchangeResponse> ExecuteExchange(HttpContext context, FormUrlEncodedContent content, string? requestedAudience)
	{
		var client = factory.CreateClient("Tidecloak");

		var resp = await client.PostAsync(
			$"protocol/openid-connect/token", content);

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

		if (!result.TryGetProperty("expires_in", out var expiresIn))
			throw new InvalidOperationException("Token exchange response did not contain an expires_in.");

		var expiresAt = DateTime.UtcNow.AddSeconds(expiresIn.GetInt32());

		// add tokens to cache
		var tokenId = context.User.GetTokenCacheId(requestedAudience);

		await cache.AddApplicationToken(tokenId, accessToken, expiresAt);

		string? applicationDoken = null;
		if (result.TryGetProperty("doken", out var doken))
		{
			// add doken to cache
			applicationDoken = doken.GetString()!;
			await cache.AddApplicationTideDoken(tokenId, applicationDoken, expiresAt);
		}

		return new TokenExchangeResponse(accessToken, applicationDoken, expiresAt);
	}
}