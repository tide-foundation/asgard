using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Tide.Asgard.AspNetCore.Authentication.mTLS;

namespace Tide.Asgard.AspNetCore.Authentication.TokenExchange;

public interface ITokenExchangeOptions
{
	public void AddClient(string clientId, string certPath);
}
public enum CertificatePathAvailability
{
	NotAvailable
}

public class TokenExchangeOptions : ITokenExchangeOptions
{
	private Dictionary<string, string> _clients = [];

	public void AddClient(string clientId, string certPath)
	{
		_clients[clientId] = certPath;
	}
	public void AddClient(string clientId, CertificatePathAvailability availability)
	{

	}
	public string GetCertPathForClient(string clientId)
	{
		if (!_clients.TryGetValue(clientId, out var certPath))
		{
			throw new InvalidOperationException($"Client {clientId} is not registered for token exchange.");
		}
		return certPath;
	}
	public IEnumerable<(string clientId, string certPath)> GetAllClients()
	{
		foreach (var kvp in _clients)
		{
			yield return (kvp.Key, kvp.Value);
		}
	}
}

