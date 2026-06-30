using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Tide.Asgard.Core.Policy;

namespace Tide.Asgard.AspNetCore.Authentication.TokenExchange
{
	// create an implementation of this service
	public interface ITokenExchangeService
	{
		Task<string> ExchangeToken(IHeaderDictionary headers, string requestingClientId, string requestedAudience);
	}
	public class TokenExchangeService(IHttpClientFactory factory) : ITokenExchangeService
	{
		public async Task<string> ExchangeToken(IHeaderDictionary headers, string requestingClientId, string requestedAudience)
		{
			// Determine if its an access token or dpop token
			//   - DPoP tokens will have both an "Authorization: DPoP ..." header and a "DPoP" proof header
			//   - Bearer tokens will only have an "Authorization: Bearer ..." header
			//   - ignore dokens as the user will never manage those themselves -> this SDK will
			var isDPoP = headers.ContainsKey("DPoP") &&
						 headers.TryGetValue("Authorization", out var auth) &&
						 auth.ToString().StartsWith("DPoP ", StringComparison.OrdinalIgnoreCase);

			if (isDPoP)
				return await ExchangeDPoPToken(headers, requestingClientId, requestedAudience);
			else
				return await ExchangeAccessToken(headers, requestingClientId, requestedAudience);
		}

		/// <summary>
		/// Looks at the Bearer Header
		/// </summary>
		/// <returns></returns>
		private async Task<string> ExchangeAccessToken(IHeaderDictionary headers, string requestingClientId, string requestedAudience)
		{
			if (!headers.TryGetValue("Authorization", out var authHeader))
				throw new UnauthorizedAccessException("Authorization header is missing.");

			var headerValue = authHeader.ToString();

			if (!headerValue.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
				throw new UnauthorizedAccessException("Authorization header is not a Bearer token.");

			var userAccessToken = headerValue["Bearer ".Length..].Trim();

			// No exchange proof validation required
			// We can continue directly to an exchange

			var client = factory.CreateClient("asgard-token-exchange-client:" + requestingClientId);

			var body = new FormUrlEncodedContent(new Dictionary<string, string>
			{
				["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
				["client_id"] = requestingClientId,
				["subject_token"] = userAccessToken,
				["subject_token_type"] = "urn:ietf:params:oauth:token-type:access_token",
				["audience"] = requestedAudience,
			});

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

			return token.GetString()!;
		}

		/// <summary>
		/// Looks at the DPoP Header
		/// </summary>
		/// <returns></returns>
		private async Task<string> ExchangeDPoPToken(IHeaderDictionary headers, string requestingClientId, string requestedAudience)
		{
throw new NotImplementedException();	
			// Ensure DPOP Authentication is enabled first - someting must first approve this dpop token



			if (!headers.TryGetValue("Authorization", out var authHeader))
				throw new UnauthorizedAccessException("Authorization header is missing.");
			if (!headers.TryGetValue("DPoP-Exchange-Proof", out var dpopExProof))
				throw new UnauthorizedAccessException("DPoP Exchange Proof header is missing.");
			if (!headers.TryGetValue("DPoP", out var dpopProofHeader))
				throw new UnauthorizedAccessException("DPoP header is missing.");

			var headerValue = authHeader.ToString();
			var exchangeProof = dpopExProof.ToString();
			var dpopProof = dpopProofHeader.ToString();

			if (!headerValue.StartsWith("DPoP ", StringComparison.OrdinalIgnoreCase))
				throw new UnauthorizedAccessException("Authorization header is not a DPoP token.");

			var userDPoPToken = headerValue["DPoP ".Length..].Trim();

			// We need the public DPoP key from the DPoP Proof to verify the user browser approved of this specific client's certificate
			var handler = new JwtSecurityTokenHandler();
			var dpopJwt = handler.ReadJwtToken(dpopProof);
			var exchangeJwt = handler.ReadJwtToken(exchangeProof);
			var jwkString = JsonSerializer.Serialize(dpopJwt.Header["jwk"]);
			var dpopKey = new JsonWebKey(jwkString);

			ValidateExchangeProof(dpopKey, exchangeJwt);


		}

		/// <summary>
		/// Looks at the Doken Header
		/// </summary>
		/// <returns></returns>
		internal async Task<string> ExchangeTideDoken(IHeaderDictionary headers)
		{
			throw new NotImplementedException();

			if (!headers.TryGetValue("Authorization", out var authHeader))
				throw new UnauthorizedAccessException("Authorization header is missing.");
			if (!headers.TryGetValue("Doken-Exchange-Proof", out var dokenExProof))
				throw new UnauthorizedAccessException("Doken Exchange Proof header is missing.");

			var headerValue = authHeader.ToString();
			var exchangeProof = dokenExProof.ToString();

			if (!headerValue.StartsWith("Doken ", StringComparison.OrdinalIgnoreCase))
				throw new UnauthorizedAccessException("Authorization header is not a Tide Doken.");

			var userDoken = headerValue["Doken ".Length..].Trim();



		}

		private void ValidateExchangeProof(SecurityKey userBoundKey, JwtSecurityToken exchangeProof)
		{
			// Ensure that the session key in the token / doken signed this client's certificate

			throw new NotImplementedException();
		}
	}
	internal sealed class TokenExchangeClientMarker { }

}
