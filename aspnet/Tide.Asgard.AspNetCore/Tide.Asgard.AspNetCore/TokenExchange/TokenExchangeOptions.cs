using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Tide.Asgard.AspNetCore.Authentication.mTLS;

namespace Tide.Asgard.AspNetCore.Authentication.TokenExchange;

public interface ITokenExchangeOptions
{
	public string ClientId { get; set; }
}

public class TokenExchangeOptions : ITokenExchangeOptions
{
	public string ClientId { get; set; } = null!;
}

