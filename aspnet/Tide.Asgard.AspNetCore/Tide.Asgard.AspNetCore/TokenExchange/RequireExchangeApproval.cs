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
using Tide.Asgard.AspNetCore.DPoP;
using Tide.Asgard.AspNetCore.DPoP.Exchange;
using Tide.Asgard.Core.Crypto.Ed25519;
using Microsoft.AspNetCore.Http;
using Cryptide.Tools;

namespace Tide.Asgard.AspNetCore.Authentication.TokenExchange;

public enum ApprovalRequirement
{
	DPoP = 0,
	TideSecuredDPoP = 1
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class RequireExchangeApproval : Attribute, IFilterFactory
{
	private readonly int _proofValidityMinutes;
	private readonly ApprovalRequirement _requirement;

	public RequireExchangeApproval(ApprovalRequirement requirement = ApprovalRequirement.DPoP, int proofValidityMinutes = 10)
	{
		_requirement = requirement;
		_proofValidityMinutes = proofValidityMinutes;
	}

	public bool IsReusable => false;

	// Runs through DI: services come from the provider, the arg comes from the attribute.
	// An IFilterFactory can only return one filter, so multiple requirements are chained
	// inside a composite that runs them in order and stops at the first one that sets a Result.
	public IFilterMetadata CreateInstance(IServiceProvider serviceProvider)
	{
		var window = TimeSpan.FromMinutes(_proofValidityMinutes);

        return _requirement switch
        {
            ApprovalRequirement.DPoP => ActivatorUtilities.CreateInstance<DPoPExchangeApprovalFilter>(serviceProvider, window),
            ApprovalRequirement.TideSecuredDPoP => ActivatorUtilities.CreateInstance<TideEnclaveApprovalFilter>(serviceProvider, window),
            _ => throw new InvalidOperationException($"Unknown approval requirement: {_requirement}"),
        };
    }
}
internal interface IChainedAuthorizationFilter : IAsyncAuthorizationFilter
{
	public IActionResult? DelayedResult { get; set; }
}
internal static class Helpers
{
	public static void RequestResourceDelegation(AuthorizationFilterContext context, string? jti, IResourceKeyProvider deviceKeyProvider)
	{
		if(!context.HttpContext.Response.Headers.Any(headers => headers.Key == "Resource-Delegation-Key"))
		{
			// challenge input: jti when present, else RFC 9449-style ath (hash of the authenticated access token).
			// distinct prefixes keep the two signing contexts domain-separated since jti is client-chosen.
			var challengeInput = jti != null
				? "resource-jti-challenge:" + jti
				: "resource-ath-challenge:" + Base64UrlEncoder.Encode(SHA256.HashData(Encoding.UTF8.GetBytes(context.HttpContext.Request.Headers.Authorization.ToString().Split(' ')[^1])));
			var deviceKey = deviceKeyProvider.GetResourceKey();
			var challenge_sig = Base64UrlEncoder.Encode(deviceKey.Sign(Encoding.UTF8.GetBytes(challengeInput)));
			context.HttpContext.Response.Headers["Resource-Delegation-Key"] = Base64UrlEncoder.Encode(deviceKey.ToSubjectPublicKeyInfoBytes());
			context.HttpContext.Response.Headers["Resource-Delegation-Challenge"] = challenge_sig;
			SetChallenge(context, AsgardErrorCode.DPoPDelegationProofNotFound.ToString(), "DPoP-Resource-Delegation header required");
		}
	}
	public static void SetChallenge(AuthorizationFilterContext context, string? error = null, string? description = null)
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

internal class TideEnclaveApprovalFilter(IResourceKeyProvider deviceKeyProvider, IAsgardCache asgardCache, ILogger<DPoPExchangeApprovalFilter> logger, TimeSpan proofValidityWindow) : DPoPExchangeApprovalFilter(deviceKeyProvider, asgardCache, logger, proofValidityWindow)
{
    public override async Task OnAuthorizationAsync(AuthorizationFilterContext context)
	{
		await base.OnAuthorizationAsync(context);

		if(context.Result != null) return; // in case there was a REALLY bad error, we won't even validate the stuff below and just return that result

		// Check if we've already got an exchanged token in cache
		var jti = context.HttpContext.User.FindFirst("jti")?.Value;
		var existingDoken = jti != null ? await _cache.GetApplicationToken("tide-doken:" + jti) : null;
		if (existingDoken != null) return; // short circuit! if we already have an exchanged token in the cache we don't need to resource delegation stuff

		if (!context.HttpContext.Request.Headers.TryGetValue("Enclave-Resource-Delegation-Signature", out var enclaveDelegationSignature))
		{
			Helpers.RequestResourceDelegation(context, jti, _deviceKeyProvider);
			context.HttpContext.Response.Headers["Require-Tide-Delegation"] = "true";
			context.Result = new UnauthorizedResult();
			return;
		}

		// ensure a tide session key is available in the token
		var tideSessionKeyValue = context.HttpContext.User.FindFirst("t.ssk")?.Value;
		if(tideSessionKeyValue == null)
		{
			Helpers.SetChallenge(context, AsgardErrorCode.TideSessionKeyNotFound.ToString());
			context.Result = new UnauthorizedResult();
			return;
		}

		if(!context.HttpContext.Items.TryGetValue("ValidatedDPoPResourceDelegationProof", out var delegationProof))
		{
			Helpers.SetChallenge(context, AsgardErrorCode.DPoPDelegationInvalid.ToString());
			context.Result = new UnauthorizedResult();
			return;
		}
		JsonWebToken delegationToken;
		try
		{
			delegationToken = TokenHandler.ReadJsonWebToken(delegationProof as string);
		}
		catch
		{
			Helpers.SetChallenge(context, AsgardErrorCode.DPoPDelegationInvalid.ToString());
			context.Result = new UnauthorizedResult();
			return;
		}

		// verify the user's delegation token was signed by the session key as well
		// we need cryptide here to deserialize the tidekey
		TideKey tideSessionKey;
		try
		{
			tideSessionKey = TideKey.From(tideSessionKeyValue);	
			string dataToVerify = delegationToken.EncodedHeader + "." + delegationToken.EncodedPayload;
			tideSessionKey.VerifyWithThrow(
				dataToVerify.FromUTF8ToByteArray(),
				enclaveDelegationSignature.ToString().FromBase64ToByteArray()
			);
		}catch
		{
			Helpers.SetChallenge(context, AsgardErrorCode.TideSessionKeyError.ToString());
			context.Result = new UnauthorizedResult();
			return;
		}

		if(DelayedResult != null)
		{
			// if base validation failed easy - don't allow filter to pass through
			// we need DelayedResult so we can check if Enclave-Resource-Delegation-Signature is required in the same http request
			// saves the client having to initally do a DPoP check, go back, then do ANOTHER enclave check, go back, then succeed
			// this way we find out all the things required in one call
			context.Result = DelayedResult;
			return;
		}
	}
}

internal class DPoPExchangeApprovalFilter : IChainedAuthorizationFilter
{
	protected static readonly JsonWebTokenHandler TokenHandler = new();
	private readonly TimeSpan _proofValidityWindow;
	private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);
	private readonly IResourceKeyProvider _deviceKeyProvider;
	private readonly ILogger<DPoPExchangeApprovalFilter> _logger;
	private readonly IAsgardCache _cache;

    public IActionResult? DelayedResult { get; set; }

    public DPoPExchangeApprovalFilter(IResourceKeyProvider deviceKeyProvider, IAsgardCache asgardCache, ILogger<DPoPExchangeApprovalFilter> logger, TimeSpan proofValidityWindow)
	{
		_deviceKeyProvider = deviceKeyProvider;
		_logger = logger;
		_proofValidityWindow = proofValidityWindow;
		_cache = asgardCache;
	}
	public virtual async Task OnAuthorizationAsync(AuthorizationFilterContext context)
	{
		// the access token must already be authenticated (DPoP scheme) - we only trust its validated claims, never the raw header
		if (context.HttpContext.User.Identity?.IsAuthenticated != true)
		{
			_logger.LogWarning("DPoP exchange approval failed: request is not authenticated");
			Helpers.SetChallenge(context);
			context.Result = new UnauthorizedResult();
			return;
		}

		// Check if we've already got an exchanged token in cache
		var jti = context.HttpContext.User.FindFirst("jti")?.Value;
		var existingToken = jti != null ? await _cache.GetApplicationToken(jti) : null;
		if (existingToken != null) return; // short circuit! if we already have an exchanged token in the cache we don't need to resource delegation stuff

		if (!context.HttpContext.Request.Headers.TryGetValue("DPoP-Resource-Delegation", out var dpopResourceDelegation))
		{
			Helpers.RequestResourceDelegation(context, jti, _deviceKeyProvider);
			DelayedResult = new UnauthorizedResult();
			return;
		}

		// Validate the dpop-resource-delegation payload
		// read the token from the header
		JsonWebToken delegationToken;
		try
		{
			delegationToken = TokenHandler.ReadJsonWebToken(dpopResourceDelegation);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "DPoP exchange approval failed: unable to parse resource delegation token");
			Helpers.SetChallenge(context, AsgardErrorCode.DPoPDelegationInvalid.ToString());
			DelayedResult = new UnauthorizedResult();
			return;
		}

		// ensure typ is "delegation+jwt"
		if (delegationToken.Typ != "delegation+jwt")
		{
			_logger.LogWarning("DPoP exchange approval failed: invalid token type '{Typ}', expected delegation+jwt", delegationToken.Typ);
			Helpers.SetChallenge(context, AsgardErrorCode.DPoPDelegationInvalid.ToString());
			DelayedResult = new UnauthorizedResult();
			return;
		}

		// Freshness: the token is client-created and has no exp, so bound it off iat instead.
		var now = DateTime.UtcNow;
		var issuedAt = delegationToken.IssuedAt; // UTC; DateTime.MinValue when iat is absent

		if (issuedAt == DateTime.MinValue)
		{
			_logger.LogWarning("DPoP exchange approval failed: resource delegation token is missing iat");
			Helpers.SetChallenge(context, AsgardErrorCode.DPoPDelegationInvalid.ToString());
			DelayedResult = new UnauthorizedResult();
			return;
		}

		// Reject proofs issued in the future (beyond allowed skew).
		if (issuedAt > now + ClockSkew)
		{
			_logger.LogWarning("DPoP exchange approval failed: resource delegation token iat is in the future (iat: {IssuedAt:O})", issuedAt);
			Helpers.SetChallenge(context, AsgardErrorCode.DPoPDelegationInvalid.ToString());
			DelayedResult = new UnauthorizedResult();
			return;
		}

		// Reject proofs older than the validity window.
		if (now - issuedAt > _proofValidityWindow + ClockSkew)
		{
			_logger.LogWarning("DPoP exchange approval failed: resource delegation token has expired (iat: {IssuedAt:O}, window: {Window})", issuedAt, _proofValidityWindow);
			Helpers.SetChallenge(context, AsgardErrorCode.DPoPDelegationInvalid.ToString());
			DelayedResult = new UnauthorizedResult();
			return;
		}

		// token has no exp as it is created by a client

		// we now get the alg + jwk from header, and use it to validate the signature of the token
		JsonWebKey jwk;
		try
		{
			jwk = new JsonWebKey(delegationToken.GetHeaderValue<JsonElement>("jwk").GetRawText());
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "DPoP exchange approval failed: missing or malformed jwk header in resource delegation token");
			Helpers.SetChallenge(context, AsgardErrorCode.DPoPDelegationInvalid.ToString());
			DelayedResult = new UnauthorizedResult();
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
			Helpers.SetChallenge(context, AsgardErrorCode.DPoPDelegationInvalid.ToString());
			DelayedResult = new UnauthorizedResult();
			return;
		}

		// ensure the deleg payload value matches this resource's device key public
		byte[] attestedResourcePublicKeyThumbprint;
		try
		{
			attestedResourcePublicKeyThumbprint = Base64UrlEncoder.DecodeBytes(delegationToken.GetPayloadValue<JsonElement>("delegate_cnf").GetProperty("spt").GetString());
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "DPoP exchange approval failed: missing or malformed deleg.jkt claim in resource delegation token");
			Helpers.SetChallenge(context, AsgardErrorCode.DPoPDelegationInvalid.ToString());
			DelayedResult = new UnauthorizedResult();
			return;
		}
		var localDevicePublicKeyThumbprint = SHA256.HashData(_deviceKeyProvider.GetResourceKey().ToSubjectPublicKeyInfoBytes());
		if (attestedResourcePublicKeyThumbprint.SequenceEqual(localDevicePublicKeyThumbprint) == false)
		{
			_logger.LogWarning("DPoP exchange approval failed: resource delegation token does not match this resource's device key");
			Helpers.SetChallenge(context, AsgardErrorCode.DPoPDelegationInvalid.ToString());
			DelayedResult = new UnauthorizedResult();
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
			Helpers.SetChallenge(context, Constants.DPoP.Error.Code.InvalidToken, Constants.DPoP.Error.Description.CnfClaimMissing);
			DelayedResult = new UnauthorizedResult();
			return;
		}

		var delegationThumbprint = ExtendedJsonWebKeyConverter.ComputeJwkThumbprint(jwk);

		if (delegationThumbprint.SequenceEqual(dpopKeyThumbprint) == false)
		{
			_logger.LogWarning("DPoP exchange approval failed: access token cnf.jkt does not match resource delegation token signing key");
			Helpers.SetChallenge(context, AsgardErrorCode.DPoPDelegationInvalid.ToString());
			DelayedResult = new UnauthorizedResult();
			return;
		}

		context.HttpContext.Items["ValidatedDPoPResourceDelegationProof"] = dpopResourceDelegation.ToString();
	}
}