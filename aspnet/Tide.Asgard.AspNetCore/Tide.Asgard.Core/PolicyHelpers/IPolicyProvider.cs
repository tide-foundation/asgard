using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tide.Asgard.Core.PolicyHelpers;

public interface IPolicyProvider
{
	Task<IReadOnlyDictionary<string, ReadOnlyMemory<byte>>> GetAllPolicies();
	Task<ReadOnlyMemory<byte>?> GetPolicy(string id);
	void SetAuthentication(string authentication);
	Task AddPolicy(string id, ReadOnlyMemory<byte> policy);
}

