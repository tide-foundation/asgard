using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tide.Asgard.AspNetCore.DPoP.EventHandlers;

namespace Tide.Asgard.AspNetCore.Authentication.SetupHelpers;

internal sealed class AsgardJwtBearerPostConfigureOptions(string authenticationScheme, Binding supportedBindings) : IPostConfigureOptions<JwtBearerOptions>
{
	public void PostConfigure(string? name, JwtBearerOptions options)
	{
		if (name != authenticationScheme) return;

		options.Events ??= new JwtBearerEvents();

		List<Func<TokenValidatedContext, Task<bool>>> validators = [];

		bool requireBinding = false;

		if (supportedBindings.HasFlag(Binding.DPoP))
		{
			requireBinding = true;
			validators.Add(ValidateDPoPBinding);

			// JwtBearer only reads the Bearer scheme itself
			var messageReceived = options.Events.OnMessageReceived;
			options.Events.OnMessageReceived = async context =>
			{
				await context.HttpContext.RequestServices.GetRequiredService<MessageReceivedHandler>().ReadDPoPToken(context);
				await messageReceived(context);
			};

			// a failed DPoP check answers with a DPoP challenge rather than the default Bearer one
			var challenge = options.Events.OnChallenge;
			options.Events.OnChallenge = async context =>
			{
				await context.HttpContext.RequestServices.GetRequiredService<ChallengeHandler>().ChallengeFailedBinding(context);
				if (!context.Handled) await challenge(context);
			};
		}

		if (supportedBindings.HasFlag(Binding.mTLS))
		{
			requireBinding = true;
			validators.Add(ValidateCertificateBinding);
		}

		var next = options.Events.OnTokenValidated;
		options.Events.OnTokenValidated = async context =>
		{
			if (requireBinding)
			{
				bool validatorSuccess = false;
				foreach (var validator in validators)
				{
					var success = await validator(context);
					if (success) validatorSuccess = true;
					if (context.Result is not null) return; // the first failed binding check ends validation
				}
				if (!validatorSuccess)
				{
					context.Fail("The token was not bound to a DPoP or mTLS authorization.");
					return;
				}
				// the hash of the token whose binding was checked, which a resource delegation's ath must match
				if (context.SecurityToken is JsonWebToken token)
					context.HttpContext.Items["Asgard.TokenHash"] = SHA256.HashData(Encoding.UTF8.GetBytes(token.EncodedToken));
			}
			await next(context); // proceed with preconfigured checks
		};

	}
	/// <summary>
	/// Fails a DPoP-bound token unless the request proves possession of the key it is bound to - the DPoP scheme, with a
	/// DPoP proof signed by the key whose thumbprint is the token's cnf jkt, as RFC 9449 defines. A token without that
	/// confirmation is left alone.
	/// </summary>
	private static async Task<bool> ValidateDPoPBinding(TokenValidatedContext context)
	{
		var handler = context.HttpContext.RequestServices.GetRequiredService<TokenValidationHandler>();
		return await handler.ValidateBinding(context);
	}
	/// <summary>
	/// Fails a certificate-bound token unless this connection's TLS client certificate is the one it is bound to - the
	/// base64url SHA-256 of the certificate, as RFC 8705 defines x5t#S256. A token without that confirmation is left alone.
	/// </summary>
	private static async Task<bool> ValidateCertificateBinding(TokenValidatedContext context)
	{
		var cnf = context.Principal?.FindFirst("cnf")?.Value;
		if (cnf is null) return false; // not proof-of-possession bound at all

		string? expected;
		try
		{
			using var document = JsonDocument.Parse(cnf);
			if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("x5t#S256", out var thumbprint))
				return false; // bound some other way, like DPoP's jkt
			expected = thumbprint.ValueKind == JsonValueKind.String ? thumbprint.GetString() : null;
		}
		catch (JsonException)
		{
			context.Fail("The token's cnf claim is not valid JSON");
			return false;
		}

		if (string.IsNullOrEmpty(expected))
		{
			context.Fail("The token's x5t#S256 confirmation is not a thumbprint");
			return false;
		}

		var certificate = await context.HttpContext.Connection.GetClientCertificateAsync(context.HttpContext.RequestAborted);
		if (certificate is null)
		{
			context.Fail("A certificate-bound token was sent without a TLS client certificate");
			return false;
		}

		var actual = Base64UrlEncoder.Encode(SHA256.HashData(certificate.RawData));
		if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(expected)))
		{
			context.Fail("A certificate-bound token was sent with a different TLS client certificate");
			return false;
		}
			

		context.HttpContext.Items["Asgard.TokenBinding"] = "mtls";
		return true;
	}
}
