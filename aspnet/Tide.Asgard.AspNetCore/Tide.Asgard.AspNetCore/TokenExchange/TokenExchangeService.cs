using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Ork.Clients.Providers;
using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Tide.Asgard.Core;
using Tide.Asgard.Core.PolicyHelpers;

namespace Tide.Asgard.AspNetCore.Authentication.TokenExchange
{
	// create an implementation of this service
	public interface ITokenExchangeService
	{
		Task<string> ExchangeToken(string requestingClientId, string requestedAudience);
		Task<string> ExchangeTideDokenForApplicationDoken();
	}
	public class TokenExchangeService(IHttpClientFactory factory, IHttpContextAccessor httpContextAccessor, IDeviceKeyProvider deviceKeyProvider) : ITokenExchangeService
	{
		private HttpContext GetHttpContext() => httpContextAccessor.HttpContext ?? throw new InvalidOperationException($"HTTP context is not available. Ensure {nameof(TokenExchangeService)} is only used in Controllers");
		public async Task<string> ExchangeToken(string requestingClientId, string requestedAudience)
		{
			// Determine if its an access token or dpop token
			//   - DPoP tokens will have both an "Authorization: DPoP ..." header and a "DPoP" proof header
			//   - Bearer tokens will only have an "Authorization: Bearer ..." header
			//   - ignore dokens as the user will never manage those themselves -> this SDK will
			var isDPoP = GetHttpContext().Request.Headers.ContainsKey("DPoP") &&
						 GetHttpContext().Request.Headers.TryGetValue("Authorization", out var auth) &&
						 auth.ToString().StartsWith("DPoP ", StringComparison.OrdinalIgnoreCase);

			if (isDPoP)
				return await ExchangeDPoPToken(requestingClientId, requestedAudience);
			else
				return await ExchangeAccessToken(requestingClientId, requestedAudience);
		}

		/// <summary>
		/// Looks at the Bearer Header
		/// </summary>
		/// <returns></returns>
		private async Task<string> ExchangeAccessToken(string requestingClientId, string requestedAudience)
		{
			if (!GetHttpContext().Request.Headers.TryGetValue("Authorization", out var authHeader))
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
		private async Task<string> ExchangeDPoPToken(string requestingClientId, string requestedAudience)
		{
throw new NotImplementedException();	
			// Ensure DPOP Authentication is enabled first - someting must first approve this dpop token



			if (!GetHttpContext().Request.Headers.TryGetValue("Authorization", out var authHeader))
				throw new UnauthorizedAccessException("Authorization header is missing.");
			if (!GetHttpContext().Request.Headers.TryGetValue("DPoP-Exchange-Proof", out var dpopExProof))
				throw new UnauthorizedAccessException("DPoP Exchange Proof header is missing.");
			if (!GetHttpContext().Request.Headers.TryGetValue("DPoP", out var dpopProofHeader))
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
		public async Task<string> ExchangeTideDokenForApplicationDoken()
		{
			/**
			 * Agent -> ClientA: 401 (Unauthorized) + Headers
	-> Tide_Exception: Doken Requested
	-> Application_Key: (SRK Cert in base64)
			 * */
			var context = GetHttpContext();

			var headers = context.Request.Headers;
			if(headers == null || !headers.TryGetValue("Application-Doken", out var applicationDoken) || !headers.TryGetValue("User-Doken", out var userDoken))
			{
				throw new AsgardException(AsgardErrorCode.DokenNotFound, headers =>
				{
					headers["Application-Key"] = Base64UrlEncoder.Encode(deviceKeyProvider.GetDeviceKey().GetPublic().ToJwk()); // maybe it's better to return a standard serialized (not SerializedComponent)
				});
			}


			var d = deviceKeyProvider.GetDeviceKeyAsString(); // WE NEED THIS DON'T REMOVE

			// WE NEED IT SO WE HAVE ACCESS TO THE DEVICE KEY TO VALIDATE THE DOKEN EXCHANGE PROOF

			// This

			





			throw new NotImplementedException();

			if (!GetHttpContext().Request.Headers.TryGetValue("Authorization", out var authHeader))
				throw new UnauthorizedAccessException("Authorization header is missing.");
			if (!GetHttpContext().Request.Headers.TryGetValue("Doken-Exchange-Proof", out var dokenExProof))
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
