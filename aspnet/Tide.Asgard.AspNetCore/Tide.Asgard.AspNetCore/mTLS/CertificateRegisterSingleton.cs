using System.Security.Cryptography.X509Certificates;

namespace Tide.Asgard.AspNetCore.Authentication.mTLS;

public sealed class CertificateRegisterSingleton
{
	private volatile ResourceCredentials? credentials;

	public bool IsRegistered => credentials != null;

	/// <summary>The credentials to hand the current handshake, or null while the enrollment is still pending.</summary>
	public ResourceCredentials? Current => credentials;

	public void Register(X509Certificate2 clientCertificate, X509Certificate2 trustBundle) 
		=> credentials = new ResourceCredentials(clientCertificate, trustBundle);

	/// <param name="ClientCertificate">Presented to Tidecloak as this resource's identity.</param>
	/// <param name="TrustBundle">The realm root CA, the only root Tidecloak's server certificate may chain to.</param>
	public sealed record ResourceCredentials(X509Certificate2 ClientCertificate, X509Certificate2 TrustBundle);
}
