using Cryptide.Key;
using Cryptide.Key.Schemes.P256;
using Cryptide.Signing;
using Cryptide.Tools;
using Microsoft.AspNetCore.Http;
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
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Tide.Asgard.AspNetCore.Authentication.SetupHelpers;
using Tide.Asgard.AspNetCore.DPoP;
using Tide.Asgard.Core;
using Tide.Asgard.Core.Crypto.Ed25519;

namespace Tide.Asgard.AspNetCore.Authentication.TokenExchange;

public enum ApprovalRequirement
{
	Basic = 0,
	TideEnabled = 1
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class RequireExchangeApproval : Attribute, IFilterFactory
{
	private readonly int _proofValidityMinutes;
	private readonly ApprovalRequirement _requirement;
	private readonly string? _exchangeTargetResource;

	public RequireExchangeApproval(ApprovalRequirement requirement = ApprovalRequirement.Basic, string? exchangeTargetResource = null, int proofValidityMinutes = 10)
	{
		_requirement = requirement;
		_exchangeTargetResource = exchangeTargetResource;
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
            ApprovalRequirement.Basic => ActivatorUtilities.CreateInstance<ResourceExchangeApprovalFilter>(serviceProvider, _exchangeTargetResource ?? "", window),
            ApprovalRequirement.TideEnabled => ActivatorUtilities.CreateInstance<TideApprovalFilter>(serviceProvider, _exchangeTargetResource ?? "", window),
            _ => throw new InvalidOperationException($"Unknown approval requirement: {_requirement}"),
        };
    }
}
internal static class Helpers
{
	public static void RequestResourceDelegation(AuthorizationFilterContext context, string tokenId, IResourceKeyProvider deviceKeyProvider, AsgardErrorCode code = AsgardErrorCode.DelegationProofNotFound)
	{
		if(!context.HttpContext.Response.Headers.Any(headers => headers.Key == "Resource-Delegation-Key"))
		{
			if (!context.HttpContext.Response.Headers.Any(headers => headers.Key == "Resource-Delegation-Key") || !context.HttpContext.Response.Headers.Any(headers => headers.Key == "Resource-Delegation-Challenge"))
			{
				// the resource delegation challenge is a signature over the token id, which is either the jti claim of the access token or the hash of the access token itself
				// challenge input: jti when present, else RFC 9449-style ath (hash of the authenticated access token).
				// distinct prefixes keep the two signing contexts domain-separated since jti is client-chosen.
				var challengeInput = "resource-token-id-challenge:" + tokenId;
				var deviceKey = deviceKeyProvider.GetResourceKey().IdentitySRK;
				var challenge_sig = Base64UrlEncoder.Encode(deviceKey.Sign(Encoding.UTF8.GetBytes(challengeInput)));
				context.HttpContext.Response.Headers["Resource-Delegation-Key"] = Base64UrlEncoder.Encode(deviceKey.ToSubjectPublicKeyInfoBytes());
				context.HttpContext.Response.Headers["Resource-Delegation-Challenge"] = challenge_sig;

				switch (code)
				{
					case AsgardErrorCode.DelegationProofNotFound:
						ResourceExchangeApprovalFilter.SetChallenge(context, code.ToString(), "Resource-Delegation header required");
						break;
					case AsgardErrorCode.TideEnclaveApprovalNotFound:
						TideApprovalFilter.SetTideChallenge(context, code.ToString(), "Enclave-Resource-Delegation-Signature header required");
						break;
					default:
						ResourceExchangeApprovalFilter.SetChallenge(context, code.ToString(), "Resource delegation required");
						break;
				}
			}
		}
	}
}

internal class TideApprovalFilter(IResourceKeyProvider deviceKeyProvider, IAsgardCache asgardCache, ILogger<ResourceExchangeApprovalFilter> logger, TimeSpan proofValidityWindow, string exchangeTargetResource) : ResourceExchangeApprovalFilter(deviceKeyProvider, asgardCache, logger, proofValidityWindow, exchangeTargetResource, true)
{
	public override async Task OnAuthorizationAsync(AuthorizationFilterContext context)
	{
		// Check if we've already got an exchanged token in cache
		var tokenId = context.HttpContext.User.GetTokenCacheId(_exchangeTargetResource);
		var existingDoken = await _cache.GetApplicationTideDoken(tokenId);
		if (existingDoken != null) return; // short circuit! if we already have an exchanged token in the cache we don't need to resource delegation stuff

		await base.OnAuthorizationAsync(context); // run base validations

		// Need to ensure ValidatedDPoPResourceDelegationProof is present in HttpContext.Items, which is set by the base DPoPExchangeApprovalFilter
		// The approval might short circuit if the cache already has a token, but for the tide delegation we also need a fresh proof for the request
		if(!context.HttpContext.Items.TryGetValue("ValidatedDPoPResourceDelegationProof", out var enclaveDelegationProof))
		{
			Helpers.RequestResourceDelegation(context, context.HttpContext.User.GetId(), _deviceKeyProvider);
			context.HttpContext.Response.Headers["Require-Tide-Delegation"] = "true";
			context.Result = new UnauthorizedResult();
			return;
		}

		if (!context.HttpContext.Request.Headers.TryGetValue("Enclave-Resource-Delegation-Signature", out var enclaveDelegationSignature))
		{
			Helpers.RequestResourceDelegation(context, context.HttpContext.User.GetId(), _deviceKeyProvider, AsgardErrorCode.TideEnclaveApprovalNotFound);
			context.HttpContext.Response.Headers["Require-Tide-Delegation"] = "true";
			context.Result = new UnauthorizedResult();
			return;
		}

		// ensure a tide session key is available in the token
		var tideSessionKeyValue = context.HttpContext.User.FindFirst("t.ssk")?.Value;
		if(tideSessionKeyValue == null)
		{
			SetTideChallenge(context, AsgardErrorCode.TideSessionKeyNotFound.ToString());
			context.Result = new UnauthorizedResult();
			return;
		}

		JsonWebToken delegationToken;
		try
		{
			delegationToken = TokenHandler.ReadJsonWebToken(enclaveDelegationProof as string);
		}
		catch
		{
			SetTideChallenge(context, AsgardErrorCode.DelegationInvalid.ToString());
			context.Result = new UnauthorizedResult();
			return;
		}

		// verify the user's delegation token was signed by the session key as well
		TideKey tideSessionKey;
		try
		{
			tideSessionKey = context.HttpContext.Items["Asgard.TokenBinding"] switch
			{
				"dpop" => TideKey.From(tideSessionKeyValue),
				"mtls" => TideKey.FromSubjectPublicKeyInfoBytes(tideSessionKeyValue.Base64UrlToBase64().FromBase64ToByteArray()),
				_ => throw new InvalidOperationException("The access token's binding was not validated"),
			};
			string dataToVerify = delegationToken.EncodedHeader + "." + delegationToken.EncodedPayload;
			TideWitnessSignatureFormat.VerifyWitnessSignature(
				tideSessionKey,
				dataToVerify.FromUTF8ToByteArray(),
				enclaveDelegationSignature.ToString().FromBase64ToByteArray()
			);
		}catch(Exception ex)
		{
			logger.LogWarning(ex, "Exchange approval failed: unable to verify tide delegation");
			SetTideChallenge(context, AsgardErrorCode.TideSessionKeyError.ToString());
			context.Result = new UnauthorizedResult();
			return;
		}

		context.HttpContext.Items["ValidatedTideDelegationSignature"] = enclaveDelegationSignature.ToString();
	}
	public static void SetTideChallenge(AuthorizationFilterContext context, string? error = null, string? description = null)
	{
		var challenge = "Tide";
		if (error != null)
		{
			challenge += $" error=\"{error}\"";
			if (description != null)
				challenge += $", error_description=\"{description}\"";
		}
		context.HttpContext.Response.Headers[Constants.DPoP.WWWAuthenticateHeader] = challenge;
	}
}

internal class ResourceExchangeApprovalFilter : IAsyncAuthorizationFilter
{
	protected static readonly JsonWebTokenHandler TokenHandler = new();
	private readonly TimeSpan _proofValidityWindow;
	private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);
	protected readonly IResourceKeyProvider _deviceKeyProvider;
	private readonly ILogger<ResourceExchangeApprovalFilter> _logger;
	protected readonly IAsgardCache _cache;
	private readonly bool _requireTideDelegation;
	protected readonly string _exchangeTargetResource;

	public ResourceExchangeApprovalFilter(IResourceKeyProvider deviceKeyProvider, IAsgardCache asgardCache, ILogger<ResourceExchangeApprovalFilter> logger, TimeSpan proofValidityWindow, string exchangeTargetResource, bool requireTideDelegation = false)
	{
		_deviceKeyProvider = deviceKeyProvider;
		_logger = logger;
		_proofValidityWindow = proofValidityWindow;
		_cache = asgardCache;
		_requireTideDelegation = requireTideDelegation;
		_exchangeTargetResource = exchangeTargetResource;
	}
	public virtual async Task OnAuthorizationAsync(AuthorizationFilterContext context)
	{
		// the access token must already be authenticated (DPoP scheme) - we only trust its validated claims, never the raw header
		if (context.HttpContext.User.Identity?.IsAuthenticated != true)
		{
			_logger.LogWarning("Exchange approval failed: request is not authenticated");
			SetChallenge(context);
			context.Result = new UnauthorizedResult();
			return;
		}

		// Check if we've already got an exchanged token in cache
		var existingToken = await _cache.GetApplicationToken(context.HttpContext.User.GetTokenCacheId(_exchangeTargetResource));
		// short circuit! if we already have an exchanged token in the cache we don't need to resource delegation stuff
		// don't short circuit if we need a tide delegation, because we need to ensure a new dpop delegation proof is present for the new request
		if (existingToken != null && _requireTideDelegation == false) return; 

		if (!context.HttpContext.Request.Headers.TryGetValue("Resource-Delegation", out var dpopResourceDelegation))
		{
			Helpers.RequestResourceDelegation(context, context.HttpContext.User.GetId(), _deviceKeyProvider);
			context.Result = new UnauthorizedResult();
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
			_logger.LogWarning(ex, "Exchange approval failed: unable to parse resource delegation token");
			SetChallenge(context, AsgardErrorCode.DelegationInvalid.ToString());
			context.Result = new UnauthorizedResult();
			return;
		}

		// ensure typ is "delegation+jwt"
		if (delegationToken.Typ != "delegation+jwt")
		{
			_logger.LogWarning("Exchange approval failed: invalid token type '{Typ}', expected delegation+jwt", delegationToken.Typ);
			SetChallenge(context, AsgardErrorCode.DelegationInvalid.ToString());
			context.Result = new UnauthorizedResult();
			return;
		}

		// Freshness: the token is client-created and has no exp, so bound it off iat instead.
		var now = DateTime.UtcNow;
		var issuedAt = delegationToken.IssuedAt; // UTC; DateTime.MinValue when iat is absent

		if (issuedAt == DateTime.MinValue)
		{
			_logger.LogWarning("Exchange approval failed: resource delegation token is missing iat");
			SetChallenge(context, AsgardErrorCode.DelegationInvalid.ToString());
			context.Result = new UnauthorizedResult();
			return;
		}

		// Reject proofs issued in the future (beyond allowed skew).
		if (issuedAt > now + ClockSkew)
		{
			_logger.LogWarning("Exchange approval failed: resource delegation token iat is in the future (iat: {IssuedAt:O})", issuedAt);
			SetChallenge(context, AsgardErrorCode.DelegationInvalid.ToString());
			context.Result = new UnauthorizedResult();
			return;
		}

		// Reject proofs older than the validity window.
		if (now - issuedAt > _proofValidityWindow + ClockSkew)
		{
			_logger.LogWarning("Exchange approval failed: resource delegation token has expired (iat: {IssuedAt:O}, window: {Window})", issuedAt, _proofValidityWindow);
			SetChallenge(context, AsgardErrorCode.DelegationInvalid.ToString());
			context.Result = new UnauthorizedResult();
			return;
		}

		// Ensure this delegation token is binded to the token used to authorize the request
		// its ath is that token's hash, which comes from the access token's validation - never from the request's headers
		var tokenHash = context.HttpContext.Items["Asgard.TokenHash"] as byte[]
			?? throw new InvalidOperationException("The access token's hash was not recorded when it was validated");
		byte[] attestedTokenHash;
		try
		{
			attestedTokenHash = Base64UrlEncoder.DecodeBytes(delegationToken.GetPayloadValue<string>("ath"));
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Exchange approval failed: missing or malformed ath claim in resource delegation token");
			SetChallenge(context, AsgardErrorCode.DelegationInvalid.ToString());
			context.Result = new UnauthorizedResult();
			return;
		}
		if (attestedTokenHash.SequenceEqual(tokenHash) == false)
		{
			_logger.LogWarning("Exchange approval failed: resource delegation token was made for another access token");
			SetChallenge(context, AsgardErrorCode.DelegationInvalid.ToString());
			context.Result = new UnauthorizedResult();
			return;
		}

		// token has no exp as it is created by a client

		// the delegation token must be signed with the key the access token is bound to. Which binding that is comes from the
		// access token's validation, which only marks a binding it has checked - never from the delegation token itself
		Binding binding = context.HttpContext.Items["Asgard.TokenBinding"] switch
		{
			"dpop" => Binding.DPoP,
			"mtls" => Binding.mTLS,
			_ => throw new InvalidOperationException("The access token's binding was not validated"),
		};
		JsonWebKey authorizingKeyJwk;
		X509Certificate2? authorizingCertificate = null;
		try
		{
			if (binding == Binding.DPoP)
			{
				authorizingKeyJwk = new JsonWebKey(delegationToken.GetHeaderValue<JsonElement>("jwk").GetRawText());
			}
			else if (binding == Binding.mTLS)
			{
				authorizingCertificate = await context.HttpContext.Connection.GetClientCertificateAsync(context.HttpContext.RequestAborted)
					?? throw new InvalidOperationException("A certificate-bound token was sent without a TLS client certificate");
				using var publicKey = authorizingCertificate.GetECDsaPublicKey() ?? throw new InvalidOperationException("The client certificate does not hold an EC key");
				authorizingKeyJwk = new JsonWebKey(P256PublicComponent.FromEcdsa(publicKey).ToJwk()); // don't judge me. it's 9:35pm
			}
			else throw new NotImplementedException("No supported delegation for binding of type: " + binding.ToString());
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Exchange approval failed: no key to check the resource delegation token's signature with");
			SetChallenge(context, AsgardErrorCode.DelegationInvalid.ToString());
			context.Result = new UnauthorizedResult();
			return;
		}
		// the jwk header must hold only the public key, as a DPoP proof's must (RFC 9449 section 4.3)
		if (authorizingKeyJwk.ContainsPrivateKey())
		{
			_logger.LogWarning("Exchange approval failed: the resource delegation token's jwk header contains a private key");
			SetChallenge(context, AsgardErrorCode.DelegationInvalid.ToString());
			context.Result = new UnauthorizedResult();
			return;
		}
		var validationParameters = new TokenValidationParameters
		{
			IssuerSigningKey = authorizingKeyJwk.ToSecurityKey(),
			ValidateIssuerSigningKey = true,
			RequireSignedTokens = true,
			ValidateIssuer = false,
			ValidateAudience = false,
			ValidateLifetime = false,                       // no exp; freshness handled above
			ValidAlgorithms = new DPoPOptions().TokenValidationParameters.ValidAlgorithms
		};

		TokenValidationResult result;
		try
		{
			result = await TokenHandler.ValidateTokenAsync(dpopResourceDelegation, validationParameters);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Exchange approval failed: DPoP resource delegation token validation failed due to null value or malformed token");
			SetChallenge(context, AsgardErrorCode.DelegationInvalid.ToString());
			context.Result = new UnauthorizedResult();
			return;
		}

		if (!result.IsValid)
		{
			_logger.LogWarning(result.Exception, "Exchange approval failed: invalid resource delegation token signature");
			SetChallenge(context, AsgardErrorCode.DelegationInvalid.ToString());
			context.Result = new UnauthorizedResult();
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
			_logger.LogWarning(ex, "Exchange approval failed: missing or malformed deleg.jkt claim in resource delegation token");
			SetChallenge(context, AsgardErrorCode.DelegationInvalid.ToString());
			context.Result = new UnauthorizedResult();
			return;
		}
		var localDevicePublicKeyThumbprint = SHA256.HashData(_deviceKeyProvider.GetResourceKey().IdentitySRK.ToSubjectPublicKeyInfoBytes());
		if (attestedResourcePublicKeyThumbprint.SequenceEqual(localDevicePublicKeyThumbprint) == false)
		{
			_logger.LogWarning("Exchange approval failed: resource delegation token does not match this resource's device key");
			SetChallenge(context, AsgardErrorCode.DelegationInvalid.ToString());
			context.Result = new UnauthorizedResult();
			return;
		}

		// ensure the authenticated token is cryptographically bound to the delegation token
		// token could be DPoP bound
		// or mTLS bound

		byte[] delegatorKeyThumbprintFromToken;
		byte[] delegatorKeyThumbprintFromAuthorizingKey;
		try
		{
			var cnf = context.HttpContext.User.FindFirst(Constants.DPoP.Cnf)?.Value;
			var cnfElement = JsonDocument.Parse(cnf!).RootElement;
			if (binding == Binding.DPoP && cnfElement.TryGetProperty("jkt", out var dpopThumbprint))
			{
				delegatorKeyThumbprintFromToken = Base64UrlEncoder.DecodeBytes(dpopThumbprint.GetString());
				delegatorKeyThumbprintFromAuthorizingKey = ExtendedJsonWebKeyConverter.ComputeJwkThumbprint(authorizingKeyJwk);
			}
			else if (binding == Binding.mTLS && cnfElement.TryGetProperty("x5t#S256", out var certThumprint))
			{
				delegatorKeyThumbprintFromToken = Base64UrlEncoder.DecodeBytes(certThumprint.GetString());

				if (authorizingCertificate is null)
				{
					throw new Exception("No client certificate to check the access token's x5t#S256 against");
				}

				delegatorKeyThumbprintFromAuthorizingKey = SHA256.HashData(authorizingCertificate.RawData);
			}
			else throw new Exception("The access token's cnf claim has no thumbprint for its binding");
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Exchange approval failed: access token has no valid cnf.jkt confirmation claim");
			SetChallenge(context, Constants.DPoP.Error.Code.InvalidToken, Constants.DPoP.Error.Description.CnfClaimMissing);
			context.Result = new UnauthorizedResult();
			return;
		}

		if (delegatorKeyThumbprintFromToken.SequenceEqual(delegatorKeyThumbprintFromAuthorizingKey) == false)
		{
			_logger.LogWarning("Exchange approval failed: access token binding does not match resource delegation token signing key");
			SetChallenge(context, AsgardErrorCode.DelegationInvalid.ToString());
			context.Result = new UnauthorizedResult();
			return;
		}

		context.HttpContext.Items["ValidatedDPoPResourceDelegationProof"] = dpopResourceDelegation.ToString();
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