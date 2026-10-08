using Microsoft.Extensions.Logging;
using System.Net;
using System.Security.Authentication;
using System.Text.Json;
using Tide.Asgard.Core;

namespace Tide.Asgard.AspNetCore.Authentication.mTLS;

public sealed class ResourceIdentityRequiredHandler(CertificateRegisterSingleton register, ILogger<ResourceIdentityRequiredHandler> logger) : DelegatingHandler
{
	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		// there is nothing to authenticate with - say so rather than opening a connection that cannot succeed
		if (!register.IsRegistered)
		{
			// the caller only sees the 503, so the reason has to reach the server console as well. It is read off the
			// exception rather than written out again here, so there is one wording of it to keep right.
			var exception = new AsgardException(AsgardErrorCode.ResourceIdentityNotRegistered, 503);
			logger.LogWarning("Refused {Method} {RequestUri}: {Reason}", request.Method, request.RequestUri, exception.Message);
			throw exception;
		}

		HttpResponseMessage response;
		try
		{
			response = await base.SendAsync(request, cancellationToken);
		}
		catch (HttpRequestException exception) when (IsHandshakeFailure(exception))
		{
			// the register only ever gains credentials, so getting here means the identity is not the problem: the
			// proxy in front of Tidecloak either has no vhost for this realm, or is serving a certificate that does
			// not cover the host this request was addressed to
			throw new HttpRequestException(
				$"The TLS handshake with Tidecloak at '{request.RequestUri?.GetLeftPart(UriPartial.Authority)}' failed. " +
				"This resource is enrolled, so check that the realm's mTLS vhost is published and that its server certificate covers that host.",
				exception);
		}

		// the handshake succeeded, so Tidecloak accepted the certificate as valid TLS - but its mTLS authenticator
		// can still reject what the certificate says about this resource
		if (response.StatusCode == HttpStatusCode.Unauthorized) await ThrowIfCertificateRejected(request, response);

		return response;
	}

	private async Task ThrowIfCertificateRejected(HttpRequestMessage request, HttpResponseMessage response)
	{
		// buffered so the caller can still read the body when this turns out not to be one of ours
		await response.Content.LoadIntoBufferAsync();
		var body = await response.Content.ReadAsStringAsync();

		if (ReadErrorCode(body) is not string errorCode || !CertificateRejections.TryGetValue(errorCode, out var code)) return;

		// the caller only sees the status and the Asgard-Exception header, so the reason - and what Tidecloak actually
		// said - has to reach the server console
		var exception = new AsgardException(code, 503);
		logger.LogError("Tidecloak rejected this resource's certificate on {Method} {RequestUri} with {ErrorCode}: {Reason} (response: {Response})",
			request.Method, request.RequestUri, errorCode, exception.Message, body);

		throw exception;
	}

	private static string? ReadErrorCode(string body)
	{
		try
		{
			using var document = JsonDocument.Parse(body);
			return document.RootElement.ValueKind == JsonValueKind.Object
				&& document.RootElement.TryGetProperty("error_code", out var errorCode)
				&& errorCode.ValueKind == JsonValueKind.String
					? errorCode.GetString()
					: null;
		}
		catch (JsonException)
		{
			return null;
		}
	}

	private static readonly Dictionary<string, AsgardErrorCode> CertificateRejections = new()
	{
		["CERT_REVOKED"] = AsgardErrorCode.ResourceCertificateRevoked,
		["INVALID_CLIENT_ID"] = AsgardErrorCode.ResourceCertificateClientMismatch,
	};

	/// <summary>True when the connection died in the handshake rather than on a response.</summary>
	private static bool IsHandshakeFailure(Exception exception)
	{
		for (var inner = exception.InnerException; inner != null; inner = inner.InnerException)
		{
			if (inner is AuthenticationException) return true;
		}
		return false;
	}
}
