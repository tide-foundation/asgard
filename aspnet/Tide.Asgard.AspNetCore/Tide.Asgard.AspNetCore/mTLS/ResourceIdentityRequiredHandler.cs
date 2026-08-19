using Microsoft.Extensions.Logging;
using System.Security.Authentication;
using Tide.Asgard.Core;

namespace Tide.Asgard.AspNetCore.Authentication.mTLS;

public sealed class ResourceIdentityRequiredHandler(CertificateRegisterSingleton register, ILogger<ResourceIdentityRequiredHandler> logger) : DelegatingHandler
{
	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		// there is nothing to authenticate with - say so rather than opening a connection that cannot succeed
		if (!register.IsRegistered)
		{
			// the caller only sees the 503, so the reason has to reach the server log as well
			logger.LogWarning(
				"Refused {Method} {RequestUri}: this resource has no approved Tide resource identity yet. Approve its certificate request in Tidecloak first.",
				request.Method, request.RequestUri);
			throw new AsgardException(AsgardErrorCode.ResourceIdentityNotRegistered, 503);
		}

		try
		{
			return await base.SendAsync(request, cancellationToken);
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
	}

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
