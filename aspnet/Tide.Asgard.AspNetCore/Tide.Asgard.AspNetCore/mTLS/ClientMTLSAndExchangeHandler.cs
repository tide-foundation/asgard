using Cryptide.Key;
using Cryptide.Signing;
using Cryptide.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ork.Clients.Providers;
using System.Buffers;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using Tide.Asgard.Core;

namespace Tide.Asgard.AspNetCore.Authentication.mTLS;

/// <summary>
/// Lets a request to another resource through only when it can be made over mutual TLS: to an https url, by a resource
/// that has its certificate to present.
/// </summary>
internal sealed class ClientMTLSAndExchangeHandler(string clientId, IHttpContextAccessor contextAccessor, IResourceKeyProvider keyProvider, CertificateRegisterSingleton register, ILogger<ClientMTLSAndExchangeHandler> logger) : DelegatingHandler
{
	protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		EnsureMTLSPossible(request);
		var bearer = EnsureBearerTokenPresent(request).GetAwaiter().GetResult();
		var response = base.Send(request, cancellationToken);
		if (HandleResourceDelegation(response, bearer).GetAwaiter().GetResult() is not var (delegation, tideDelegation)) return response;
		// Replay the request with the delegation now
		response.Dispose();
		request.Headers.Remove("Resource-Delegation");
		request.Headers.Add("Resource-Delegation", delegation);
		if (tideDelegation != null)
		{
			request.Headers.Remove("Enclave-Resource-Delegation-Signature");
			request.Headers.Add("Enclave-Resource-Delegation-Signature", tideDelegation);
		}
		return base.Send(request, cancellationToken);
	}

	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		EnsureMTLSPossible(request);
		var bearer = await EnsureBearerTokenPresent(request);
		var response = await base.SendAsync(request, cancellationToken);
		if (await HandleResourceDelegation(response, bearer) is not var (delegation, tideDelegation)) return response;
		// Replay the request with the delegation now
		response.Dispose();
		request.Headers.Remove("Resource-Delegation");
		request.Headers.Add("Resource-Delegation", delegation);
		if(tideDelegation != null)
		{
			request.Headers.Remove("Enclave-Resource-Delegation-Signature");
			request.Headers.Add("Enclave-Resource-Delegation-Signature", tideDelegation);
		}
		return await base.SendAsync(request, cancellationToken);
	}

	private void EnsureMTLSPossible(HttpRequestMessage request)
	{
		// over http the TLS settings are never used - the request would quietly go out without mTLS
		if (request.RequestUri?.Scheme != Uri.UriSchemeHttps)
			throw new InvalidOperationException($"The Asgard client for '{clientId}' only makes mTLS requests, so '{request.RequestUri}' has to be an https url.");

		// there is nothing to authenticate with - say so rather than opening a connection that cannot succeed
		if (!register.IsRegistered)
		{
			// the caller only sees the 503, so the reason has to reach the server console as well
			var exception = new AsgardException(AsgardErrorCode.ResourceIdentityNotRegistered, 503);
			logger.LogWarning("Refused {Method} {RequestUri}: {Reason}", request.Method, request.RequestUri, exception.Message);
			throw exception;
		}
	}
	private async Task<string> EnsureBearerTokenPresent(HttpRequestMessage request)
	{
		var service = contextAccessor.HttpContext?.RequestServices.GetRequiredService<IAspAsgardService>()
			?? throw new InvalidOperationException($"The Asgard client for '{clientId}' can only add a token inside an HTTP request.");
		var clientToken = await service.ExchangeContextTokenForApplicationToken(clientId);
		request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", clientToken); // TODO: get the default scheme from the builder, not hardcode it
		return clientToken;
	}
	private async Task<(string resourceDelegaton, string? tideDelegation)?> HandleResourceDelegation(HttpResponseMessage response, string bearer)
	{
		var context = contextAccessor.HttpContext ?? throw new InvalidOperationException($"The Asgard client for '{clientId}' can only handle resource delegation inside an HTTP request.");

		if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && 
			response.Headers.TryGetValues("Resource-Delegation-Key", out var resourceDelegationKey) &&
			response.Headers.TryGetValues("Resource-Delegation-Challenge", out var delegationChallenge))
		{
			var wwwAuthenticate = response.Headers.WwwAuthenticate.ToString();
			if (wwwAuthenticate.Contains($"error=\"{AsgardErrorCode.DelegationProofNotFound}\""))
			{
				// Verify the resource server is asking US for a delegation
				var challengeMessage = $"resource-token-id-challenge:{GetTokenJti(bearer)}";
				var targetResourceKey = TideKey.FromSubjectPublicKeyInfoBytes(Convert.FromBase64String(resourceDelegationKey.First().Base64UrlToBase64()));
				targetResourceKey.VerifyWithThrow(
					challengeMessage.FromUTF8ToByteArray(),
					Convert.FromBase64String(delegationChallenge.First().Base64UrlToBase64()),
					"Resource challenge signature verification failed"
				);
				// Create the signed delegation token using our resource P256 key
				(var resourceKey, var tideKey) = keyProvider.GetResourceKey();
				if (resourceKey.GetPublic().GetScheme().SchemeType != TideComponentSchemeType.P256) throw new Exception("Resource key type is not P256. Other key types are not supported");
				var header = new Dictionary<string, object>
				{
					{ "alg", "ES256" },
					{ "typ", "delegation+jwt" },
					{ "bind", "mtls" }
				};
				var payload = new Dictionary<string, object>
				{
					{ "jti", Guid.NewGuid().ToString() },
					{ "iat", Cryptide.Tools.Utils.GetEpochSeconds().ToString() },
					{ "delegate_cnf", new { spt = SHA256.HashData(targetResourceKey.ToSubjectPublicKeyInfoBytes()).ToBase64String().Base64ToBase64Url()} },
					{ "ath", SHA256.HashData(bearer.FromUTF8ToByteArray()).ToBase64String().Base64ToBase64Url() }
				};
				var unsignedToken = $"{JsonSerializer.Serialize(header).FromUTF8ToByteArray().ToBase64String().Base64ToBase64Url()}.{JsonSerializer.Serialize(payload).FromUTF8ToByteArray().ToBase64String().Base64ToBase64Url()}";
				var resourceSignature = resourceKey.Sign(unsignedToken.FromUTF8ToByteArray()).ToBase64String().Base64ToBase64Url();
				var signedDelegationToken = $"{unsignedToken}.{resourceSignature}";
				// If Tide delegaton required too, provide it as well
				string? tideDelegation = null;
				if (response.Headers.TryGetValues("Require-Tide-Delegation", out var tideDelegationRequired) && tideDelegationRequired.First() == "true")
				{
					var witnessFormat = new TideWitnessSignatureFormat(Cryptide.Tools.Utils.GetEpochSeconds(), unsignedToken.FromUTF8ToByteArray());
					var tideDelegationSignature = tideKey.Sign(witnessFormat.Format());
					tideDelegation = witnessFormat.CreateApprovalEnvelope(tideDelegationSignature).ToBase64String();
				}

				return (signedDelegationToken, tideDelegation);
			}
		}
		return null;
	}
	private static string GetTokenJti(string token)
	{
		// only the payload is decoded, and read only as far as the jti - the one string allocated is the jti itself
		var span = token.AsSpan();
		var start = span.IndexOf('.') + 1;
		var length = start == 0 ? -1 : span[start..].IndexOf('.');
		if (length < 0) throw new ArgumentException("The token is not a JWS", nameof(token));
		var encodedPayload = span.Slice(start, length);

		var maxLength = Base64Url.GetMaxDecodedLength(encodedPayload.Length);
		byte[]? rented = null;
		Span<byte> buffer = maxLength <= 1024 ? stackalloc byte[1024] : (rented = ArrayPool<byte>.Shared.Rent(maxLength));
		try
		{
			var reader = new Utf8JsonReader(buffer[..Base64Url.DecodeFromChars(encodedPayload, buffer)]);
			if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw new ArgumentException("The token's payload is not a JSON object", nameof(token));
			while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
			{
				if (reader.ValueTextEquals("jti"u8))
				{
					reader.Read();
					if (reader.TokenType == JsonTokenType.String && reader.GetString() is { Length: > 0 } jti) return jti;
					break;
				}
				reader.Skip(); // past this claim's value, however deeply it nests
			}
		}
		finally
		{
			if (rented != null) ArrayPool<byte>.Shared.Return(rented);
		}
		throw new InvalidOperationException("The token does not contain a 'jti' claim.");
	}
}
