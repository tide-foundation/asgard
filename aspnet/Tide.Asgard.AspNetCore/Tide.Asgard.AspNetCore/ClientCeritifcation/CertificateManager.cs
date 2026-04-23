using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;

namespace Tide.Asgard.AspNetCore.Authentication.ClientCeritifcation;

internal class CertificateManager(string credentialPath)
{
	public X509Certificate2 RetrieveCertificate()
	{
		return new X509Certificate2(credentialPath);
	}
	public void GenerateCertificate()
	{

	}
}
