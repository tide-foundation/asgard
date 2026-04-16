using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;

namespace Tide.Asgard.AspNetCore.Authentication.mTLS;

public class MTLSOptions
{
	public string? Name { get; set; }
	public X509Certificate2? X509Certificate2 { get; set; }
	public Uri? BaseUri { get; set; }
}