using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Tide.Asgard.AspNetCore.Authentication.mTLS;

namespace Tide.Asgard.AspNetCore.Authentication.TokenExchange;

public class TokenExchangeMTLSOptions : MTLSOptions
{
	public const string ClientName = "xchange-mtls-client";
	public new string Name { get; } = ClientName;
}
