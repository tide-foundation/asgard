// Copyright (c) Okta, Inc. and/or its affiliates. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE-APACHE-2.0 in the project root.
// Modifications Copyright (c) Tide Foundation Limited.

using Microsoft.Extensions.Logging;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Tide.Asgard.AspNetCore.Authentication.mTLS;

/// <summary>
/// Checks the certificates on either end of an mTLS connection against the realm's root CA. A rejected certificate is
/// logged by its thumbprint: until it chains to the realm root CA, its subject is whatever the peer wrote into it.
/// </summary>
internal static class RealmCertificateValidation
{
	/// <summary>
	/// Validates Tidecloak's server certificate: it has to be issued by the realm's root CA, for TLS server
	/// authentication, to Tidecloak - CN=realm_{<paramref name="realm"/>} - and cover the host in the request url.
	/// </summary>
	internal static bool ValidateTidecloakCertificate(X509Certificate? serverCertificate, SslPolicyErrors errors, CertificateRegisterSingleton.ResourceCredentials credentials, string realm, ILogger logger)
	{
		bool Reject(string reason)
		{
			logger.LogWarning("Rejected the Tidecloak server certificate {Thumbprint}: {Reason}", serverCertificate?.GetCertHashString(), reason);
			return false;
		}

		if (serverCertificate == null) return Reject("the server presented no certificate");

		// the trust bundle only answers "who signed this" - a wrong hostname is still a failure
		if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable) || errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
			return Reject("the host in the request url is not covered by its SANs");

		using var serverCertificateCopy = X509CertificateLoader.LoadCertificate(serverCertificate.Export(X509ContentType.Cert));
		using var trustBundleChain = CreateRealmChain(credentials.TrustBundle);
		trustBundleChain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1")); // id-kp-serverAuth

		if (!trustBundleChain.Build(serverCertificateCopy))
			return Reject($"it is not a server certificate issued by the realm trust bundle ({DescribeStatus(trustBundleChain)})");

		var expectedSubject = $"CN=realm_{realm}";
		if (serverCertificateCopy.SubjectName.Name != expectedSubject)
			return Reject($"it was issued to '{serverCertificateCopy.SubjectName.Name}', not to {expectedSubject}");

		return true;
	}

	/// <summary>
	/// Validates another resource's server certificate: it has to be issued by the realm's root CA, for TLS server
	/// authentication, to the resource that was meant to be called - CN=client_{<paramref name="clientId"/>}. Resource
	/// certificates carry no DNS names, so the identity the realm CA signed is checked instead of the request's host.
	/// </summary>
	internal static bool ValidateResourceServerCertificate(X509Certificate? serverCertificate, CertificateRegisterSingleton.ResourceCredentials credentials, string clientId, ILogger logger)
	{
		// the realm CA only certifies client ids of this shape, so one outside it is a mistake in the caller - and could
		// otherwise smuggle extra attributes into the subject compared below
		if (!Utils.IsCertifiableClientId(clientId))
			throw new ArgumentException($"'{clientId}' is not a client id the realm CA certifies", nameof(clientId));

		bool Reject(string reason)
		{
			logger.LogWarning("Rejected the server certificate {Thumbprint} of resource '{ClientId}': {Reason}", serverCertificate?.GetCertHashString(), clientId, reason);
			return false;
		}

		if (serverCertificate == null) return Reject("the server presented no certificate");

		using var serverCertificateCopy = X509CertificateLoader.LoadCertificate(serverCertificate.Export(X509ContentType.Cert));

		// the TLS stack's own verdict is not consulted: it always holds a name mismatch, as there is no DNS name to match,
		// and its chain errors come from the machine trust store - the chain is built again here against the realm root
		// CA alone
		using var trustBundleChain = CreateRealmChain(credentials.TrustBundle);
		// the realm CA issues resource certificates for TLS server and client authentication, and this one is acting as a
		// server
		trustBundleChain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1")); // id-kp-serverAuth

		if (!trustBundleChain.Build(serverCertificateCopy))
			return Reject($"it is not a server certificate issued by the realm trust bundle ({DescribeStatus(trustBundleChain)})");

		// The realm CA only signs a resource's request when its subject is exactly CN=client_<the attested client id>, so
		// the subject names the resource. The CA also signs Tidecloak's own server certificate, CN=realm_<name>, which
		// the client_ prefix keeps out.
		var expectedSubject = $"CN=client_{clientId}";
		if (serverCertificateCopy.SubjectName.Name != expectedSubject)
			return Reject($"it was issued to '{serverCertificateCopy.SubjectName.Name}', not to {expectedSubject}");

		return true;
	}

	/// <summary>
	/// Validates the client certificate another resource connects with: it has to be issued by the realm's root CA for
	/// TLS client authentication. Which resource it belongs to is not decided here - a certificate-bound token sent on the
	/// connection is checked against this exact certificate separately.
	/// </summary>
	internal static bool ValidateResourceClientCertificate(X509Certificate2 clientCertificate, CertificateRegisterSingleton.ResourceCredentials credentials, ILogger logger)
	{
		using var trustBundleChain = CreateRealmChain(credentials.TrustBundle);
		// the realm CA issues resource certificates for TLS server and client authentication, and this one is acting as a
		// client. Tidecloak's own certificate, from the same CA, is for server authentication only.
		trustBundleChain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.2")); // id-kp-clientAuth
		// Nothing is added to the ExtraStore: the realm CA is pathlen 0, so it signs every resource certificate itself and
		// there is no intermediate to bring in.

		if (trustBundleChain.Build(clientCertificate)) return true;

		// anyone who can reach the port gets this far, so it is a debug line only - as are the failed handshakes Kestrel
		// logs itself
		if (logger.IsEnabled(LogLevel.Debug))
			logger.LogDebug("Rejected the client certificate {Thumbprint}: it is not a client certificate issued by the realm trust bundle ({ChainStatus})", clientCertificate.Thumbprint, DescribeStatus(trustBundleChain));
		return false;
	}

	/// <summary>
	/// The chain policy for the check the TLS stack runs itself before it hands the certificate to the callbacks here.
	/// Its verdict is not used - each callback builds the chain again against the realm root CA - but it still runs, so it
	/// must not go to the network either. See <see cref="CreateRealmChain"/>.
	/// </summary>
	internal static X509ChainPolicy CreateHandshakeChainPolicy() => new()
	{
		RevocationMode = X509RevocationMode.NoCheck,
		DisableCertificateDownloads = true,
	};

	/// <summary>
	/// A chain that trusts the realm's root CA and nothing else, and never goes to the network: the realm CA publishes no
	/// CRL/OCSP endpoint, so a revocation check would only ever fail as unknown, and it signs every certificate itself, so
	/// there is no intermediate to download. An issuer url in a certificate a peer sends would only have this resource
	/// make requests on the peer's behalf - and hold the handshake while they time out.
	/// </summary>
	private static X509Chain CreateRealmChain(X509Certificate2 trustBundle)
	{
		var chain = new X509Chain();
		chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
		chain.ChainPolicy.CustomTrustStore.Add(trustBundle);
		chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
		chain.ChainPolicy.DisableCertificateDownloads = true;
		return chain;
	}

	/// <summary>Why a chain did not build, for a log line.</summary>
	private static string DescribeStatus(X509Chain chain) => string.Join(", ", chain.ChainStatus.Select(status => status.Status));
}
