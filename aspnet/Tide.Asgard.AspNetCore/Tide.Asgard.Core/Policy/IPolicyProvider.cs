using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tide.Asgard.Core.Policy;

public interface IPolicyProvider
{
	Task<IReadOnlyDictionary<string, ReadOnlyMemory<byte>>> GetAllPolicies();
	Task<ReadOnlyMemory<byte>?> GetPolicy(string id);
	void SetAuthentication(string authentication);
}

/// <summary>
/// Requires AddOptionalProviderAuthentication to be called by IPolicyCache that uses this provider
/// </summary>
public class TidecloakPolicyProvider : IPolicyProvider
{
	private static readonly HttpClient _httpClient = new HttpClient();
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

		// GET /admin/realms/{realm}/iga/role-policies  (authenticated-only, returns a JSON array)
		var requestUri = $"{_baseUrl}/admin/realms/{Uri.EscapeDataString(_realm!)}/iga/role-policies";
		using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);

		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TidecloakToken);

		using var response = await _httpClient.SendAsync(request);
		response.EnsureSuccessStatusCode();

		using var stream = await response.Content.ReadAsStreamAsync();
		var policies = await JsonSerializer
			.DeserializeAsync<List<RolePolicyRepresentation>>(stream) ?? [];

		var result = new Dictionary<string, ReadOnlyMemory<byte>>(policies.Count);
		foreach (var policy in policies)
		{
			// the "name" key is the policy ID; skip malformed rows defensively
			if (string.IsNullOrEmpty(policy.Name) || string.IsNullOrEmpty(policy.Policy))
				continue;

			// "policy" field is Base64 of the serialized Policy bytes
			result[policy.Name] = Convert.FromBase64String(policy.Policy);
		}

		return result;

	}

	public async Task<ReadOnlyMemory<byte>?> GetPolicy(string id)
	{
		if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));
		if (string.IsNullOrEmpty(TidecloakToken))
			throw new InvalidOperationException("Authentication token not set. Call SetAuthentication first.");

		// GET /admin/realms/{realm}/iga/role-policies/name/{name}  (authenticated-only)
		var requestUri = $"{_baseUrl}/admin/realms/{Uri.EscapeDataString(_realm!)}/iga/role-policies/name/{Uri.EscapeDataString(id)}";
		using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);

		// add tidecloak token to the authorization header
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TidecloakToken);

		using var response = await _httpClient.SendAsync(request);

		// no policy with that name
		if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
			return null;

		response.EnsureSuccessStatusCode();

		using var stream = await response.Content.ReadAsStreamAsync();
		var policy = await JsonSerializer
			.DeserializeAsync<RolePolicyRepresentation>(stream);

		// "policy" field is Base64 of the serialized Policy bytes
		if (string.IsNullOrEmpty(policy?.Policy))
			return null;

		return Convert.FromBase64String(policy.Policy);
	}
}
internal sealed class RolePolicyRepresentation
{
	[JsonPropertyName("name")]
	public string? Name { get; set; }

	[JsonPropertyName("policy")]
	public string? Policy { get; set; }
}

// assume a TidecloakPolicyProvider will be created -> stores policies on tidecloak
