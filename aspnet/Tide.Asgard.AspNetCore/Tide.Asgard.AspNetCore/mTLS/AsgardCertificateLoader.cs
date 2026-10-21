using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using static Tide.Asgard.AspNetCore.Authentication.SetupHelpers.TidecloakAdapter;

namespace Tide.Asgard.AspNetCore.Authentication.mTLS;

internal static class AsgardCertificateLoader
{
	public static X509Certificate2 LoadRootCertificate(IConfiguration configuration)
	{
		var paths = GetResourceIdentityPaths(configuration.GetSection("Asgard"));

		if (!File.Exists(paths.RootCaPath))
		{
			throw new FileNotFoundException("Root certificate file not found.", paths.RootCaPath);
		}

		return X509CertificateLoader.LoadCertificateFromFile(paths.RootCaPath);
	}
}
