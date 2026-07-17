using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tide.Asgard.Core.PolicyHelpers;

namespace Tide.Asgard.AspNetCore.Authentication;

/// <summary>
/// Requires AddOptionalProviderAuthentication to be called by IPolicyCache that uses this provider
/// </summary>
public class TidecloakPolicyProvider(IHttpClientFactory factory) : IPolicyProvider
{
	private string? _baseUrl;
	private string? _realm;
	private string? TidecloakToken { get; set; }
	public void SetAuthentication(string authentication)
	{
		// ensure authentication is a token
		if (string.IsNullOrEmpty(authentication)) throw new ArgumentNullException(nameof(authentication));
		var parts = authentication.Split('.');
		if (parts.Length != 3 || parts.Any(string.IsNullOrEmpty))
			throw new ArgumentException("Authentication must be a valid JWT.", nameof(authentication));

		string issuer;
		var json = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(parts[1]));
		using (var doc = JsonDocument.Parse(json))
		{
			if (!doc.RootElement.TryGetProperty("iss", out var iss) || iss.ValueKind != JsonValueKind.String)
				throw new ArgumentException("JWT payload has no 'iss' claim.", nameof(authentication));
			issuer = iss.GetString()!;
		}

		// derive base URL + realm from the issuer: "{baseUrl}/realms/{realm}"
		const string marker = "/realms/";
		var idx = issuer.IndexOf(marker, StringComparison.Ordinal);
		if (idx < 0)
			throw new ArgumentException("JWT 'iss' is not a Keycloak realm issuer URL.", nameof(authentication));

		_baseUrl = issuer.Substring(0, idx).TrimEnd('/');
		_realm = issuer.Substring(idx + marker.Length).Trim('/');
		if (string.IsNullOrEmpty(_baseUrl) || string.IsNullOrEmpty(_realm))
			throw new ArgumentException("Could not derive base URL and realm from the JWT issuer.", nameof(authentication));

		TidecloakToken = authentication;
	}

	public async Task<IReadOnlyDictionary<string, ReadOnlyMemory<byte>>> GetAllPolicies()
	{
		if (string.IsNullOrEmpty(TidecloakToken))
			throw new InvalidOperationException("Authentication token not set. Call SetAuthentication first.");

		// GET /realms/{realm}/tide-policy/all  (any authenticated realm user, returns a JSON array)
		var requestUri = $"{_baseUrl}/realms/{Uri.EscapeDataString(_realm!)}/tide-policy/all";
		using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);

		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TidecloakToken);

		var client = factory.CreateClient("Tidecloak");
		using var response = await client.SendAsync(request);
		response.EnsureSuccessStatusCode();

		using var stream = await response.Content.ReadAsStreamAsync();
		var policies = await JsonSerializer
			.DeserializeAsync<List<TidePolicyRepresentation>>(stream) ?? [];

		var result = new Dictionary<string, ReadOnlyMemory<byte>>(policies.Count);
		foreach (var policy in policies)
		{
			// "id" is the policy ID; skip malformed rows defensively
			if (string.IsNullOrEmpty(policy.Id) || string.IsNullOrEmpty(policy.Data))
				continue;

			// "data" is Base64 of the serialized Policy bytes
			result[policy.Id] = Convert.FromBase64String(policy.Data);
		}

		return result;
	}

	public async Task<ReadOnlyMemory<byte>?> GetPolicy(string id)
	{
		if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));
		if (string.IsNullOrEmpty(TidecloakToken))
			throw new InvalidOperationException("Authentication token not set. Call SetAuthentication first.");

		// GET /admin/realms/{realm}/iga/role-policies/name/{name}  (authenticated-only)
		var requestUri = $"{_baseUrl}/realms/{Uri.EscapeDataString(_realm!)}/tide-policy/find/{Uri.EscapeDataString(id)}";
		using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);

		// add tidecloak token to the authorization header
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TidecloakToken);

		var client = factory.CreateClient("Tidecloak");
		using var response = await client.SendAsync(request);

		// no policy with that name
		if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
			return null;

		response.EnsureSuccessStatusCode();

		using var stream = await response.Content.ReadAsStreamAsync();
		var policy = await JsonSerializer
			.DeserializeAsync<TidePolicyRepresentation>(stream);

		// "policy" field is Base64 of the serialized Policy bytes
		if (string.IsNullOrEmpty(policy?.Data))
			return null;

		return Convert.FromBase64String(policy.Data);
	}

	public async Task AddPolicy(string id, ReadOnlyMemory<byte> policy)
	{
		var changeRequestId = await AddPolicyWithChangeRequest(id, policy);
		if (changeRequestId != null) throw new Exception("Policy was not added directly, a change request was created instead. Change Request ID: " + changeRequestId);
	}
	public async Task<string?> AddPolicyWithChangeRequest(string id, ReadOnlyMemory<byte> policy)
	{
		if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));
		if (string.IsNullOrEmpty(TidecloakToken))
			throw new InvalidOperationException("Authentication token not set. Call SetAuthentication first.");

		// POST /realms/{realm}/tide-policy/add  (any authenticated realm user)
		// form fields: id = policy id, data = Base64 of the serialized Policy bytes
		var requestUri = $"{_baseUrl}/realms/{Uri.EscapeDataString(_realm!)}/tide-policy/add";
		using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
		{
			Content = new FormUrlEncodedContent(new[]
			{
				new KeyValuePair<string, string>("id", id),
				new KeyValuePair<string, string>("data", Convert.ToBase64String(policy.Span)),
			}),
		};

		// add tidecloak token to the authorization header
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TidecloakToken);

		var client = factory.CreateClient("Tidecloak");
		using var response = await client.SendAsync(request);
		response.EnsureSuccessStatusCode();

		using var stream = await response.Content.ReadAsStreamAsync();
		var result = await JsonSerializer
			.DeserializeAsync<AddPolicyResponse>(stream);

		// IGA on  -> body has "changeRequestId" (pending approval)
		// IGA off -> body has only "id" (written directly); no CR, so null
		return result?.ChangeRequestId;
	}
}
internal sealed class TidePolicyRepresentation
{
	[JsonPropertyName("id")]
	public string? Id { get; set; }

	[JsonPropertyName("data")]
	public string? Data { get; set; }   // Base64 of the serialized Policy bytes

	[JsonPropertyName("realmId")]
	public string? RealmId { get; set; }

	[JsonPropertyName("createdAt")]
	public long? CreatedAt { get; set; }

	[JsonPropertyName("notes")]
	public string? Notes { get; set; }
}
internal sealed class AddPolicyResponse
{
	[JsonPropertyName("changeRequestId")]
	public string? ChangeRequestId { get; set; }

	[JsonPropertyName("id")]
	public string? Id { get; set; }
}
