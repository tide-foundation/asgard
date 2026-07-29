using Cryptide.Key;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Ork.Clients.Providers;
using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tide.Asgard.Core;
using Tide.Asgard.Core.Crypto.Ed25519;

namespace Tide.Asgard.AspNetCore.DPoP.Exchange;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class RequireDPoPExchangeApproval : Attribute, IFilterFactory
{
	private readonly int _proofValidityMinutes;

	public RequireDPoPExchangeApproval(int proofValidityMinutes = 10)
	{
		_proofValidityMinutes = proofValidityMinutes;
	}

	public bool IsReusable => false;

	// Runs through DI: services come from the provider, the arg comes from the attribute.
	public IFilterMetadata CreateInstance(IServiceProvider serviceProvider)
	{
		return ActivatorUtilities.CreateInstance<DPoPExchangeApprovalFilter>(
			serviceProvider,
			TimeSpan.FromMinutes(_proofValidityMinutes));
	}
}

internal sealed class DPoPExchangeApprovalFilter : IAsyncAuthorizationFilter
{
	private static readonly JsonWebTokenHandler TokenHandler = new();
	private readonly TimeSpan _proofValidityWindow;
	private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);
	private readonly IResourceKeyProvider _deviceKeyProvider;
	private readonly ILogger<DPoPExchangeApprovalFilter> _logger;
	private readonly IAsgardCache _cache;
	public DPoPExchangeApprovalFilter(IResourceKeyProvider deviceKeyProvider, IAsgardCache asgardCache, ILogger<DPoPExchangeApprovalFilter> logger, TimeSpan proofValidityWindow)
	{
		_deviceKeyProvider = deviceKeyProvider;
		_logger = logger;
		_proofValidityWindow = proofValidityWindow;
		_cache = asgardCache; 
	}
	public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
	{
		// the access token must already be authenticated (DPoP scheme) - we only trust its validated claims, never the raw header
		if (context.HttpContext.User.Identity?.IsAuthenticated != true)
		{
			_logger.LogWarning("DPoP exchange approval failed: request is not authenticated");
			SetChallenge(context);
			context.Result = new UnauthorizedResult();
			return;
		}

		// Check if we've already got an exchanged token in cache
		var jti = context.HttpContext.User.FindFirst("jti")?.Value;
		var existingToken = jti != null ? await _cache.GetApplicationToken(jti) : null;
		if (existingToken != null) return; // short circuit! if we already have an exchanged token in the cache we don't need to resource delegation stuff

		// RFC 6648 deprecates the X- conevention for header names
		if (!context.HttpContext.Request.Headers.TryGetValue("DPoP-Resource-Delegation", out var dpopResourceDelegation))
		{
			_logger.LogWarning("DPoP exchange approval failed: missing DPoP-Resource-Delegation header");
			// challenge input: jti when present, else RFC 9449-style ath (hash of the authenticated access token).
			// distinct prefixes keep the two signing contexts domain-separated since jti is client-chosen.
			var challengeInput = jti != null
				? "dpop-jti-challenge:" + jti
				: "dpop-ath-challenge:" + Base64UrlEncoder.Encode(SHA256.HashData(Encoding.UTF8.GetBytes(context.HttpContext.Request.Headers.Authorization.ToString().Split(' ')[^1])));
			var deviceKey = _deviceKeyProvider.GetResourceKey();
			var challenge_sig = Base64UrlEncoder.Encode(deviceKey.Sign(Encoding.UTF8.GetBytes(challengeInput)));
			context.HttpContext.Response.Headers["DPoP-Delegation-Key"] = Base64UrlEncoder.Encode(deviceKey.ToSubjectPublicKeyInfoBytes());
			context.HttpContext.Response.Headers["DPoP-Delegation-Challenge"] = challenge_sig;
			SetChallenge(context, Constants.DPoP.Error.Code.DelegationRequired, "DPoP-Resource-Delegation header required");
			context.Result = new UnauthorizedResult();
			return;
		}

		// Validate the dpop-resource-delegation payload
		// read the token from the header
		JsonWebToken token;
		try
		{
			token = TokenHandler.ReadJsonWebToken(dpopResourceDelegation);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "DPoP exchange approval failed: unable to parse resource delegation token");
			SetChallenge(context, Constants.DPoP.Error.Code.InvalidDelegation);
			context.Result = new UnauthorizedResult();
			return;
		}

		// ensure typ is "delegation+jwt"
		if (token.Typ != "delegation+jwt")
		{
			_logger.LogWarning("DPoP exchange approval failed: invalid token type '{Typ}', expected delegation+jwt", token.Typ);
			SetChallenge(context, Constants.DPoP.Error.Code.InvalidDelegation);
			context.Result = new UnauthorizedResult();
			return;
		}

		// Freshness: the token is client-created and has no exp, so bound it off iat instead.
		var now = DateTime.UtcNow;
		var issuedAt = token.IssuedAt; // UTC; DateTime.MinValue when iat is absent

		if (issuedAt == DateTime.MinValue)
		{
			_logger.LogWarning("DPoP exchange approval failed: resource delegation token is missing iat");
			SetChallenge(context, Constants.DPoP.Error.Code.InvalidDelegation);
			context.Result = new UnauthorizedResult();
			return;
		}

		// Reject proofs issued in the future (beyond allowed skew).
		if (issuedAt > now + ClockSkew)
		{
			_logger.LogWarning("DPoP exchange approval failed: resource delegation token iat is in the future (iat: {IssuedAt:O})", issuedAt);
			SetChallenge(context, Constants.DPoP.Error.Code.InvalidDelegation);
			context.Result = new UnauthorizedResult();
			return;
		}

		// Reject proofs older than the validity window.
		if (now - issuedAt > _proofValidityWindow + ClockSkew)
		{
			_logger.LogWarning("DPoP exchange approval failed: resource delegation token has expired (iat: {IssuedAt:O}, window: {Window})", issuedAt, _proofValidityWindow);
			SetChallenge(context, Constants.DPoP.Error.Code.InvalidDelegation);
			context.Result = new UnauthorizedResult();
			return;
		}

		// token has no exp as it is created by a client

		// we now get the alg + jwk from header, and use it to validate the signature of the token
		JsonWebKey jwk;
		try
		{
			jwk = new JsonWebKey(token.GetHeaderValue<JsonElement>("jwk").GetRawText());
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "DPoP exchange approval failed: missing or malformed jwk header in resource delegation token");
			SetChallenge(context, Constants.DPoP.Error.Code.InvalidDelegation);
			context.Result = new UnauthorizedResult();
			return;
		}
		var validationParameters = new TokenValidationParameters
		{
			IssuerSigningKey = jwk.ToSecurityKey(),
			ValidateIssuerSigningKey = true,
			RequireSignedTokens = true,
			ValidateIssuer = false,
			ValidateAudience = false,
			ValidateLifetime = false,                       // no exp; freshness handled above
			ValidAlgorithms = new DPoPOptions().TokenValidationParameters.ValidAlgorithms
		};

		var result = await TokenHandler.ValidateTokenAsync(dpopResourceDelegation, validationParameters);

		if (!result.IsValid)
		{
			_logger.LogWarning(result.Exception, "DPoP exchange approval failed: invalid resource delegation token signature");
			SetChallenge(context, Constants.DPoP.Error.Code.InvalidDelegation);
			context.Result = new UnauthorizedResult();
			return;
		}

		// ensure the deleg payload value matches this resource's device key public
		byte[] attestedResourcePublicKeyThumbprint;
		try
		{
			attestedResourcePublicKeyThumbprint = Base64UrlEncoder.DecodeBytes(token.GetPayloadValue<JsonElement>("delegate_cnf").GetProperty("spt").GetString());
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "DPoP exchange approval failed: missing or malformed deleg.jkt claim in resource delegation token");
			SetChallenge(context, Constants.DPoP.Error.Code.InvalidDelegation);
			context.Result = new UnauthorizedResult();
			return;
		}
		var localDevicePublicKeyThumbprint = SHA256.HashData(_deviceKeyProvider.GetResourceKey().ToSubjectPublicKeyInfoBytes());
		if(attestedResourcePublicKeyThumbprint.SequenceEqual(localDevicePublicKeyThumbprint) == false)
		{
			_logger.LogWarning("DPoP exchange approval failed: resource delegation token does not match this resource's device key");
			SetChallenge(context, Constants.DPoP.Error.Code.InvalidDelegation);
			context.Result = new UnauthorizedResult();
			return;
		}

		// ensure the authenticated access token's cnf thumbprint matches the delegation token's jwk thumbprint
		// cnf comes from the validated principal, not the raw Authorization header
		byte[] dpopKeyThumbprint;
		try
		{
			var cnf = context.HttpContext.User.FindFirst(Constants.DPoP.Cnf)?.Value;
			dpopKeyThumbprint = Base64UrlEncoder.DecodeBytes(JsonDocument.Parse(cnf!).RootElement.GetProperty("jkt").GetString());
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "DPoP exchange approval failed: access token has no valid cnf.jkt confirmation claim");
			SetChallenge(context, Constants.DPoP.Error.Code.InvalidToken, Constants.DPoP.Error.Description.CnfClaimMissing);
			context.Result = new UnauthorizedResult();
			return;
		}

		var delegationThumbprint = ExtendedJsonWebKeyConverter.ComputeJwkThumbprint(jwk);

		if (delegationThumbprint.SequenceEqual(dpopKeyThumbprint) == false)
		{
			_logger.LogWarning("DPoP exchange approval failed: access token cnf.jkt does not match resource delegation token signing key");
			SetChallenge(context, Constants.DPoP.Error.Code.InvalidDelegation);
			context.Result = new UnauthorizedResult();
			return;
		}

		context.HttpContext.Items["ValidatedDPoPResourceDelegationProof"] = dpopResourceDelegation.ToString();
	}

	// RFC 7235: every 401 must carry a WWW-Authenticate challenge. error/description are
	// RFC 9449-style auth-params; error is null for a plain "authenticate first" challenge.
	private static void SetChallenge(AuthorizationFilterContext context, string? error = null, string? description = null)
	{
		var challenge = Constants.DPoP.Error.DPoPScheme;
		if (error != null)
		{
			challenge += $" error=\"{error}\"";
			if (description != null)
				challenge += $", error_description=\"{description}\"";
		}
		context.HttpContext.Response.Headers[Constants.DPoP.WWWAuthenticateHeader] = challenge;
	}
}