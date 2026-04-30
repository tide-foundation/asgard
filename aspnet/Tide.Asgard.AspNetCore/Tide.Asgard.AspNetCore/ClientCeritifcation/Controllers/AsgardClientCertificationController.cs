using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Tide.Asgard.AspNetCore.Authentication;

namespace Tide.Asgard.AspNetCore.Authentication.ClientCertification.Controllers
{
	[ApiController]
	[Route("[controller]")]
	[Authorize(Policy = TidecloakDashboardAuthenticationSchemes.ClientCertificationPolicy)]
	public class AsgardClientCertificationController(IOptionsMonitor<ClientCertificationOptions> optionsMonitor) : ControllerBase
	{
		[HttpGet("generate/{clientId}")]
		public IActionResult Generate([FromRoute] string clientId)
		{
			var options = optionsMonitor.Get(clientId);

			var current = options.RegistrationStatus;
			if (current is RegistrationStatus.Registered or RegistrationStatus.Certifying)
				return Conflict($"Cannot generate: client is currently {current}.");

			using var rsa = RSA.Create(2048);

			var request = new CertificateRequest(
				new X500DistinguishedName($"CN={clientId}"),
				rsa,
				HashAlgorithmName.SHA256,
				RSASignaturePadding.Pkcs1);

			// Self-signed placeholder so the private key persists in standard PFX form.
			// Certify replaces the cert portion with the CA-signed one, keeping this key.
			using var placeholder = request.CreateSelfSigned(
				DateTimeOffset.UtcNow.AddMinutes(-1),
				DateTimeOffset.UtcNow.AddDays(1));

			Directory.CreateDirectory(options.CredentialPath);
			var pfxPath = Path.Combine(options.CredentialPath, ClientCertificationOptions.CredentialFileName);
			System.IO.File.WriteAllBytes(pfxPath, placeholder.Export(X509ContentType.Pfx));

			return Content(request.CreateSigningRequestPem(), "application/x-pem-file");
		}

		[HttpPost("cerify/{clientId}")]
		public async Task<IActionResult> Certify([FromRoute] string clientId)
		{
			var options = optionsMonitor.Get(clientId);

			// Atomically claim the certify slot. Only one caller can hold Certifying at a time;
			// any concurrent call (or a call after Registered/Error) loses the CAS and is rejected.
			if (!options.TrySwitchStatus(RegistrationStatus.Unregistered, RegistrationStatus.Certifying))
				return Conflict($"Cannot certify: client is currently {options.RegistrationStatus}.");

			var succeeded = false;
			try
			{
				var pfxPath = Path.Combine(options.CredentialPath, ClientCertificationOptions.CredentialFileName);
				if (!System.IO.File.Exists(pfxPath))
					return Conflict("No pending certificate request. Call /generate first.");

				using var bodyReader = new StreamReader(Request.Body);
				var signedCertPem = await bodyReader.ReadToEndAsync();

				if (string.IsNullOrWhiteSpace(signedCertPem))
					return BadRequest("Signed certificate body is empty.");

				X509Certificate2 signedCert;
				try
				{
					signedCert = X509Certificate2.CreateFromPem(signedCertPem);
				}
				catch (CryptographicException)
				{
					return BadRequest("Could not parse the signed certificate.");
				}

				using (signedCert)
				using (var placeholder = new X509Certificate2(pfxPath, (string?)null, X509KeyStorageFlags.Exportable))
				{
					using var rsa = placeholder.GetRSAPrivateKey()
						?? throw new InvalidOperationException("Placeholder PFX is missing its RSA private key.");

					using var combined = signedCert.CopyWithPrivateKey(rsa);
					System.IO.File.WriteAllBytes(pfxPath, combined.Export(X509ContentType.Pfx));
				}

				succeeded = options.TrySwitchStatus(RegistrationStatus.Certifying, RegistrationStatus.Registered);
				return Ok();
			}
			finally
			{
				// Roll back to Unregistered on any non-success path so the client can retry.
				// Won't fire if we already committed to Registered.
				if (!succeeded)
					options.TrySwitchStatus(RegistrationStatus.Certifying, RegistrationStatus.Unregistered);
			}
		}
	}
}
