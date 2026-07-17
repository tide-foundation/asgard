using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Tide.Asgard.AspNetCore.Authentication.DPoP;

/// <summary>
///     Attaches a DPoP proof (RFC 9449) to every outgoing request and retries once when the
///     server demands a nonce, mirroring keycloak-js' secureFetch behaviour. A token in the
///     Authorization header is bound into the proof via 'ath' and upgraded to the DPoP scheme.
/// </summary>
public class DPoPProofMessageHandler(IDPoPProofGenerator proofGenerator, DPoPNonceStore nonceStore) : DelegatingHandler
{
	private const string ProofHeader = "DPoP";
	private const string NonceHeader = "DPoP-Nonce";
	private const string UseDPoPNonceError = "use_dpop_nonce";

	protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
		=> SendAsync(request, cancellationToken).GetAwaiter().GetResult();

	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		if (request.RequestUri is null)
			throw new InvalidOperationException("Cannot create a DPoP proof for a request without a URI.");

		// buffer so the content can be sent a second time if the server demands a nonce
		if (request.Content != null)
			await request.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);

		var origin = request.RequestUri.GetLeftPart(UriPartial.Authority);

		var response = await SendWithProofAsync(request, nonceStore.Get(origin), cancellationToken).ConfigureAwait(false);
		var serverNonce = ReadNonce(response);
		if (serverNonce != null)
			nonceStore.Set(origin, serverNonce);

		if (serverNonce != null && await IsUseDPoPNonceErrorAsync(response, cancellationToken).ConfigureAwait(false))
		{
			response.Dispose();
			response = await SendWithProofAsync(request, serverNonce, cancellationToken).ConfigureAwait(false);
			var retryNonce = ReadNonce(response);
			if (retryNonce != null)
				nonceStore.Set(origin, retryNonce);
		}

		return response;
	}

	private Task<HttpResponseMessage> SendWithProofAsync(HttpRequestMessage request, string? nonce, CancellationToken cancellationToken)
	{
		var htu = request.RequestUri!.GetLeftPart(UriPartial.Path);
		var htm = request.Method.Method;

		string? accessToken = null;
		var auth = request.Headers.Authorization;
		if (auth?.Parameter != null &&
			(auth.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) ||
			 auth.Scheme.Equals("DPoP", StringComparison.OrdinalIgnoreCase)))
		{
			accessToken = auth.Parameter;
			request.Headers.Authorization = new AuthenticationHeaderValue("DPoP", accessToken);
		}

		var proof = proofGenerator.CreateProof(htu, htm, accessToken, nonce);
		request.Headers.Remove(ProofHeader);
		request.Headers.TryAddWithoutValidation(ProofHeader, proof);

		return base.SendAsync(request, cancellationToken);
	}

	private static string? ReadNonce(HttpResponseMessage response)
		=> response.Headers.TryGetValues(NonceHeader, out var values) ? values.FirstOrDefault() : null;

	/// <summary>
	///     Detects both flavours of nonce challenge: a token endpoint responds 400 with
	///     "error": "use_dpop_nonce" in the body (RFC 9449 section 8), a resource server
	///     responds 401 with the error in the WWW-Authenticate header (section 9).
	/// </summary>
	private static async Task<bool> IsUseDPoPNonceErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
	{
		if (response.StatusCode == HttpStatusCode.Unauthorized)
		{
			return response.Headers.WwwAuthenticate.Any(challenge =>
				challenge.Scheme.Equals("DPoP", StringComparison.OrdinalIgnoreCase) &&
				challenge.Parameter?.Contains($"error=\"{UseDPoPNonceError}\"", StringComparison.OrdinalIgnoreCase) == true);
		}

		if (response.StatusCode == HttpStatusCode.BadRequest)
		{
			// buffered read keeps the body available for the caller when this turns out to be a different error
			await response.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
			try
			{
				using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
				return body.RootElement.TryGetProperty("error", out var error) && error.GetString() == UseDPoPNonceError;
			}
			catch (JsonException)
			{
				return false;
			}
		}

		return false;
	}
}
