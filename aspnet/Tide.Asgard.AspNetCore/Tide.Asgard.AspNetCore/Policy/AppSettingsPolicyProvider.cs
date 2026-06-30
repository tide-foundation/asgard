using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Text;
using Tide.Asgard.Core.Policy;

namespace Tide.Asgard.AspNetCore.Authentication.Policy;
public class AppSettingsPolicyProvider(IConfiguration config) : IPolicyProvider
{
	private Dictionary<string, ReadOnlyMemory<byte>> Policies { get; set; } = new Dictionary<string, ReadOnlyMemory<byte>>();

	public Task LoadPoliciesAsync()
	{		
		foreach(var policy in config.GetSection("Policies").GetChildren())
		{
			var id = policy["Id"] ?? throw new NullReferenceException("Policy object is appsettings has null value for id");
			var data = policy["Data"] ?? throw new NullReferenceException("Policy object is appsettings has null value for data"); ;
			Policies[id] = Convert.FromBase64String(data);
		}
		return Task.CompletedTask;
	}
	public async Task<IReadOnlyDictionary<string, ReadOnlyMemory<byte>>> GetAllPolicies()
	{
		return Policies.AsReadOnly();
	}

	public async Task<ReadOnlyMemory<byte>?> GetPolicy(string id)
	{
		return Policies[id];
	}
}