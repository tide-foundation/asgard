using System.Collections.Concurrent;

namespace Tide.Asgard.AspNetCore.Authentication.DPoP;

/// <summary>
///     Remembers the most recent DPoP-Nonce issued by each server origin (RFC 9449 sections 8 and 9).
/// </summary>
public class DPoPNonceStore
{
	private readonly ConcurrentDictionary<string, string> _nonces = new(StringComparer.OrdinalIgnoreCase);

	public string? Get(string origin) => _nonces.TryGetValue(origin, out var nonce) ? nonce : null;

	public void Set(string origin, string nonce) => _nonces[origin] = nonce;
}
