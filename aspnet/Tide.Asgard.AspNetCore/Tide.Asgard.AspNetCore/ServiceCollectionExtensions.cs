// Copyright (c) Okta, Inc. and/or its affiliates. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE-APACHE-2.0 in the project root.
// Modifications Copyright (c) Tide Foundation Limited.

using Cryptide.Hashing;
using Cryptide.Key;
using Cryptide.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Ork.Clients;
using Ork.Clients.Providers;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Resources;
using System.Runtime.Intrinsics.Arm;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Tide.Asgard.AspNetCore.Authentication.DPoP;
using Tide.Asgard.AspNetCore.Authentication.Middleware;
using Tide.Asgard.AspNetCore.Authentication.TokenExchange;
using Tide.Asgard.Core;
using Tide.Asgard.Core.Crypto.Ed25519;
using Tide.Asgard.Core.KeyHelpers.Ed25519;
using Tide.Asgard.Core.PolicyHelpers;

namespace Tide.Asgard.AspNetCore.Authentication;

/// <summary>
///     Contains
///     <see href="https://docs.microsoft.com/en-us/dotnet/api/microsoft.extensions.dependencyinjection.iservicecollection">IServiceCollection</see>
///     extension(s) for registering Asgard.
/// </summary>
public static class ServiceCollectionExtensions
{
	/// <summary>
	/// Enables OAuth 2.0 Token Exchange in your application.
	/// </summary>
	/// <returns></returns>
	/// <exception cref="InvalidOperationException"></exception>
	public static IServiceCollection AddTokenExchange(
		this IServiceCollection services,
		string tidecloakHttpsDomain,
		string realm,
		string clientId,
		string[] certificatePaths
		)
	{
		ArgumentNullException.ThrowIfNull(services);
		ArgumentNullException.ThrowIfNull(certificatePaths);

		var domainUri = new Uri(tidecloakHttpsDomain);
		if (domainUri.Scheme != Uri.UriSchemeHttps)
		{
			throw new InvalidOperationException($"The provided domain '{tidecloakHttpsDomain}' is not a valid HTTPS URL.");
		}
		var httpsRealmDomain = domainUri.GetLeftPart(UriPartial.Authority) + $"/realms/{realm}/";

		X509CertificateCollection certList = [];
		foreach (var certPath in certificatePaths)
		{
			if (!File.Exists(certPath))
			{
				throw new InvalidOperationException($"Certificate file not found at path: {certPath}");
			}
			certList.Add(X509CertificateLoader.LoadPkcs12FromFile(certPath, password: null));
		}

		services.AddHttpClient("Tidecloak", client =>
		{
			client.BaseAddress = new Uri(httpsRealmDomain);
		})
		.ConfigurePrimaryHttpMessageHandler(() =>
		{
			return new SocketsHttpHandler
			{
				SslOptions = new SslClientAuthenticationOptions
				{
					ClientCertificates = certList
				}
			};
		});

		services.TryAddSingleton<ITokenExchangeService, TokenExchangeService>();

		return services;
	}

	// add support for the configuration to be a section to allow for multiple token exchange clients to be configured in the same app
	public static IServiceCollection AddTokenExchangeForClient(
		this IServiceCollection services,
		IConfigurationSection configurationSection
		)
	{
		ArgumentNullException.ThrowIfNull(services);
		ArgumentNullException.ThrowIfNull(configurationSection);
		string clientId = GetClientId(configurationSection);
		string clientSecret = configurationSection["credentials:secret"] ?? throw new InvalidOperationException("Missing required configuration: credentials:secret");
		string baseRealmUrl = GetBaseRealmUrl(configurationSection);

		var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));

		services.AddHttpClient("Tidecloak", client =>
		{
			client.BaseAddress = new Uri(baseRealmUrl);
			client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
		});

		services.TryAddScoped<ITokenExchangeService, TokenExchangeService>();
		services.AddHttpContextAccessor();

		return services;
	}

	/// <summary>
	/// Enables OAuth 2.0 Token Exchange in your application.
	/// </summary>
	/// <returns></returns>
	/// <exception cref="InvalidOperationException"></exception>
	public static IServiceCollection AddTokenExchange(
		this IServiceCollection services,
		IConfiguration configuration
		)
	{
		ArgumentNullException.ThrowIfNull(services);
		ArgumentNullException.ThrowIfNull(configuration);

		return AddTokenExchangeForClient(services, configuration.GetSection("Keycloak") ?? throw new InvalidOperationException("Missing required configuration section: Keycloak"));
	}

	/// <summary>
	/// Default implementation of Asgard registration. Uses a FileDeviceKeyProvider to store the device key in a file.
	/// </summary>
	/// <param name="services"></param>
	/// <param name="asgardConfiguration"></param>
	/// <returns></returns>
	public static IServiceCollection AddAsgard(
		this IServiceCollection services,
		IConfiguration asgardConfiguration,
		ResourceAuthenticationMode resourceAuthMode
		)
	{
		var keyPath = asgardConfiguration.GetSection("Keycloak")["private_key_path"] ?? Utils.RESOURCE_KEY_DEFAULT_PATH;
		return AddAsgard(services, asgardConfiguration, new FileResourceKeyProvider(keyPath), resourceAuthMode);
	}
	/// <summary>
	/// Default implementation of Asgard registration. Uses the provided IDeviceKeyProvider to store the device key.
	/// </summary>
	/// <param name="services"></param>
	/// <param name="config"></param>
	/// <param name="resourceKeyProvider"></param>
	/// <returns></returns>
	/// <exception cref="Exception"></exception>
	public static IServiceCollection AddAsgard(
		this IServiceCollection services,
		IConfiguration config,
		IResourceKeyProvider resourceKeyProvider,
		ResourceAuthenticationMode resourceAuthMode
		)
	{
		// add the tide client manager provider
		var configurationSection = config.GetSection("Keycloak") ?? throw new InvalidOperationException("Missing required configuration section: Keycloak");

		string baseRealmUrl = GetBaseRealmUrl(configurationSection);

		var tideClientManagerProvider = new TideClientManagerProvider(
			baseRealmUrl,
			resourceKeyProvider,
			config["homeOrkUrl"],
			config["networkThreshold"] == null ? null : int.Parse(config["networkThreshold"]!)
			);
		services.AddSingleton(tideClientManagerProvider);

		// add http context accessor
		services.AddHttpContextAccessor();

		// add token exchange service
		services.TryAddSingleton<ITokenExchangeService, TokenExchangeService>();

		// add default cache service
		services.AddScoped<IAsgardCache, AspDefaultAsgardCache>();

		// add default tidecloak policy provider
		services.AddScoped<IPolicyProvider, TidecloakPolicyProvider>();

		// add asgard service
		services.AddScoped<IAspAsgardService, AspAsgardService>();

		// add asgrd exception handler
		services.AddExceptionHandler<AsgardExceptionHandler>();

		// add asgard request handler
		services.AddTransient<AsgardMessageHandler>();
		services.AddHttpClient("Asgard")
			.AddHttpMessageHandler<AsgardMessageHandler>();

		// add the tidecloak client with correct credentials 
		ConfigureConfidentialTidecloakClient(services, configurationSection, resourceAuthMode, resourceKeyProvider).GetAwaiter().GetResult();

		// add device key provider
		services.AddSingleton(resourceKeyProvider);

		return services;
	}

	private static async Task ConfigureConfidentialTidecloakClient(IServiceCollection services, IConfigurationSection configurationSection, ResourceAuthenticationMode authMode, IResourceKeyProvider resourceKeyProvider)
	{
		string baseRealmUrl = GetBaseRealmUrl(configurationSection);
		string realm = GetRealm(configurationSection);
		var paths = GetResourceIdentityPaths(configurationSection);

		switch (authMode)
		{
			case ResourceAuthenticationMode.AutoMTLSEnrollment:
				var enrolledIdentity = await EnrollResourceIdentity(configurationSection, baseRealmUrl, paths, resourceKeyProvider);
				ConfigureMTLSTidecloakClient(services, baseRealmUrl, realm, enrolledIdentity, resourceKeyProvider.GetResourceKey());
				break;

			case ResourceAuthenticationMode.MTLS:
				// no enrollment in this mode - the credentials have to be on disk already
				var identity = LoadResourceIdentity(configurationSection, paths, resourceKeyProvider.GetResourceKey())
					?? throw new InvalidOperationException($"{ResourceAuthenticationMode.MTLS} requires a signed certificate at '{paths.CertificatePath}' and a root CA at '{paths.RootCaPath}'. Use {ResourceAuthenticationMode.AutoMTLSEnrollment} to enroll them.");
				ConfigureMTLSTidecloakClient(services, baseRealmUrl, realm, identity, resourceKeyProvider.GetResourceKey());
				break;
			default:
				throw new NotSupportedException($"Unsupported {nameof(ResourceAuthenticationMode)}: {authMode}");
		}
	}

	/// <summary>
	/// Brings the resource identity to a usable state: mints the resource key if there isn't one, requests a
	/// certificate with the enrollment token if none has been requested yet, and collects the signed certificate once
	/// Tidecloak has approved it. Safe to call on every startup - each step is skipped once its output is on disk.
	/// </summary>
	private static async Task<ResourceIdentity> EnrollResourceIdentity(IConfigurationSection configurationSection, string baseRealmUrl, ResourceIdentityPaths paths, IResourceKeyProvider resourceKeyProvider)
	{
		var resourceKey = GetOrCreateResourceKey(resourceKeyProvider);

		// enrolled on an earlier run - nothing to do
		var existingIdentity = LoadResourceIdentity(configurationSection, paths, resourceKey);
		if (existingIdentity != null) return existingIdentity;

		using var httpClient = new HttpClient();

		// nothing requested yet -> submit a signing request for the resource key
		bool justCreated = false;
		if (!File.Exists(paths.CertificateRequestPath))
		{
			var enrollmentToken = configurationSection["enrollment_token"] ?? throw new InvalidOperationException("'enrollment_token' required in configuration");

			using var signingKey = ToECDsa(resourceKey);
			var certificateRequest = new CertificateRequest(new X500DistinguishedName($"CN=client_{GetClientId(configurationSection)}"), signingKey, HashAlgorithmName.SHA256);

			httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", enrollmentToken);
			var enrollResponse = await httpClient.PostAsync(baseRealmUrl + "tide-server-identity/request", new StringContent(certificateRequest.CreateSigningRequestPem()));
			if (!enrollResponse.IsSuccessStatusCode)
			{
				throw enrollResponse.StatusCode switch
				{
					HttpStatusCode.Unauthorized => new InvalidOperationException("Invalid enrollment token"),
					HttpStatusCode.BadRequest => new InvalidOperationException($"Issue with making enrollment request: {await enrollResponse.Content.ReadAsStringAsync()}"),
					_ => new InvalidOperationException($"Failed to enroll certificate: {enrollResponse.StatusCode}"),
				};
			}

			// written only once the request is in - its presence is what stops the next startup from enrolling again
			File.WriteAllText(paths.CertificateRequestPath, certificateRequest.CreateSigningRequestPem());
			httpClient.DefaultRequestHeaders.Authorization = null;
			justCreated = true;
		}

		// requested, but not collected yet -> ask Tidecloak whether it has been signed
		var statusResponse = await httpClient.GetAsync(baseRealmUrl + $"tide-server-identity/status?fingerprint={Uri.EscapeDataString(GetResourceKeyFingerprint(resourceKey))}");
		if (!statusResponse.IsSuccessStatusCode)
		{
			throw statusResponse.StatusCode switch
			{
				HttpStatusCode.NotFound => new InvalidOperationException($"Tidecloak has no record of the certificate request in '{paths.CertificateRequestPath}'. Delete it to enroll again."),
				_ => new InvalidOperationException($"Failed to find certificate: {statusResponse.StatusCode}"),
			};
		}

		var resourceIdentityResponse = JsonSerializer.Deserialize<ResourceIdentityResponse>(await statusResponse.Content.ReadAsStringAsync()) ?? throw new InvalidOperationException("Failed to deserialize resource identity response");
		if (resourceIdentityResponse.status != ACTIVE_RESOURCE_IDENTITY_STATUS)
		{
			if (justCreated)
			{
				Console.WriteLine($"Certificate request submitted to Tidecloak. Approve it in Tidecloak, then start the resource again.");
				Environment.Exit(0);
			}
			throw new InvalidOperationException($"The resource identity is '{resourceIdentityResponse.status}', not '{ACTIVE_RESOURCE_IDENTITY_STATUS}'. Approve it in Tidecloak, then start the resource again.");
		}

		var certificate = X509Certificate2.CreateFromPem(resourceIdentityResponse.GetCertificate());
		var trustBundle = X509Certificate2.CreateFromPem(resourceIdentityResponse.GetRootCa());
		VerifyResourceIdentity(certificate, trustBundle, configurationSection, resourceKey);

		File.WriteAllBytes(paths.CertificatePath, certificate.Export(X509ContentType.Cert));
		File.WriteAllBytes(paths.RootCaPath, trustBundle.Export(X509ContentType.Cert));

		File.Delete(paths.CertificateRequestPath);

		return new ResourceIdentity(certificate, trustBundle);
	}

	/// <summary>
	/// Reads a previously enrolled identity from disk, or null when the enrollment never completed. Both halves are
	/// required - a certificate without its trust bundle counts as not enrolled, so the pair gets collected again.
	/// </summary>
	private static ResourceIdentity? LoadResourceIdentity(IConfigurationSection configurationSection, ResourceIdentityPaths paths, TideKey resourceKey)
	{
		if (!File.Exists(paths.CertificatePath) || !File.Exists(paths.RootCaPath)) return null;

		var certificate = X509CertificateLoader.LoadCertificateFromFile(paths.CertificatePath);
		var trustBundle = X509CertificateLoader.LoadCertificateFromFile(paths.RootCaPath);
		VerifyResourceIdentity(certificate, trustBundle, configurationSection, resourceKey);

		return new ResourceIdentity(certificate, trustBundle);
	}

	/// <summary>
	/// Rejects an identity that does not belong to this resource: the certificate has to certify the resource key, and
	/// the trust bundle has to be the realm's own root CA as published in the Tidecloak configuration.
	/// </summary>
	private static void VerifyResourceIdentity(X509Certificate2 certificate, X509Certificate2 trustBundle, IConfigurationSection configurationSection, TideKey resourceKey)
	{
		using var resourcePublicKey = ToECDsa(resourceKey);
		if (!certificate.PublicKey.ExportSubjectPublicKeyInfo().SequenceEqual(resourcePublicKey.ExportSubjectPublicKeyInfo()))
		{
			throw new InvalidOperationException("Certificate public key does not match resource key public key");
		}

		var issuerKey = configurationSection.GetEd25519IssuerKey() as EdDsaSecurityKey ?? throw new InvalidOperationException("Configured Tidecloak issuer key is not an Ed25519 key");
		if (!trustBundle.PublicKey.ExportSubjectPublicKeyInfo().SequenceEqual(issuerKey.EdDsa.ExportSubjectPublicKeyInfo()))
		{
			throw new InvalidOperationException("Trust bundle public key does not match realm public key");
		}
	}

	/// <summary>
	/// Registers the "Tidecloak" http client for mutual TLS. The resource's signed certificate is presented as the
	/// client certificate, and Tidecloak's server certificate has to chain to the realm's root CA rather than to
	/// whatever the machine trust store happens to contain.
	/// </summary>
	private static void ConfigureMTLSTidecloakClient(IServiceCollection services, string baseRealmUrl, string realm, ResourceIdentity identity, TideKey resourceKey)
	{
		// built once rather than per handler: the factory rebuilds the handler periodically, and on Windows every
		// PKCS#12 load leaves another key behind in the store
		var clientCertificate = CreateClientCertificate(identity.Certificate, resourceKey);
		var trustBundle = identity.TrustBundle;

		// The reverse proxy in front of Tidecloak serves each realm on its own "{realm}.client.{domain}" vhost and
		// selects that realm's certificate from the server name.
		var configuredUri = new Uri(baseRealmUrl);
		var realmUri = new UriBuilder(configuredUri)
		{
			Scheme = Uri.UriSchemeHttps,
			Host = $"{realm}.client.{configuredUri.Host}",
			Port = 8443
		}.Uri;

		Console.WriteLine($"[mTLS] 'Tidecloak' client registered for {realmUri} (configured as '{baseRealmUrl}')");
		Console.WriteLine($"[mTLS]   client cert: subject='{clientCertificate.Subject}' issuer='{clientCertificate.Issuer}' thumbprint={clientCertificate.Thumbprint} hasPrivateKey={clientCertificate.HasPrivateKey} notBefore={clientCertificate.NotBefore:O} notAfter={clientCertificate.NotAfter:O}");
		Console.WriteLine($"[mTLS]   trust bundle: subject='{trustBundle.Subject}' issuer='{trustBundle.Issuer}' thumbprint={trustBundle.Thumbprint}");

		services.AddHttpClient("Tidecloak", client =>
		{
			client.BaseAddress = realmUri;
		})
		.ConfigurePrimaryHttpMessageHandler(() =>
		{
			return new SocketsHttpHandler
			{
				SslOptions = new SslClientAuthenticationOptions
				{
					ClientCertificates = [clientCertificate],
					RemoteCertificateValidationCallback = (_, serverCertificate, chain, errors) => ChainsToTrustBundle(serverCertificate, chain, errors, trustBundle)
				}
			};
		});
	}

	/// <summary>
	/// The resource key, minting and persisting one on first run. The provider is the single source of truth for it -
	/// the certificate and trust bundle on disk are only ever read back against whatever key it holds.
	/// </summary>
	private static TideKey GetOrCreateResourceKey(IResourceKeyProvider resourceKeyProvider)
	{
		try
		{
			return resourceKeyProvider.GetResourceKey();
		}
		catch (FileNotFoundException)
		{
			var resourceKey = TideKey.NewKey(TideComponentSchemeType.P256);
			resourceKeyProvider.SetResourceKey(resourceKey);
			return resourceKey;
		}
	}

	/// <summary>
	/// The resource key as an <see cref="ECDsa"/>. Only a P-256 resource key can carry an mTLS identity.
	/// </summary>
	private static ECDsa ToECDsa(TideKey resourceKey)
	{
		var key = ECDsa.Create();
		try
		{
			key.ImportPkcs8PrivateKey(resourceKey.GetPrivate().GetRawData(), out _);
			return key;
		}
		catch (CryptographicException e)
		{
			key.Dispose();
			throw new InvalidOperationException("Resource key is not a P-256 private key and cannot be used for mTLS", e);
		}
	}

	/// <summary>
	/// The fingerprint Tidecloak files a certificate request under - SHA-256 over the SubjectPublicKeyInfo. Derived
	/// from the resource key itself, so it can be recomputed without reading the request back.
	/// </summary>
	private static string GetResourceKeyFingerprint(TideKey resourceKey)
	{
		using var key = ToECDsa(resourceKey);
		return "SHA256:" + Convert.ToBase64String(SHA256.HashData(key.ExportSubjectPublicKeyInfo())).Base64ToBase64Url();
	}

	/// <summary>
	/// Pairs the signed certificate with the resource key so it can be used for TLS client authentication - the
	/// certificate on its own holds only the public key.
	/// </summary>
	private static X509Certificate2 CreateClientCertificate(X509Certificate2 certificate, TideKey resourceKey)
	{
		using var privateKey = ToECDsa(resourceKey);
		using var certificateWithKey = certificate.CopyWithPrivateKey(privateKey);

		// CopyWithPrivateKey leaves the key ephemeral, which SslStream cannot use on Windows - the PKCS#12 round trip
		// gives it a key handle it will accept. No-op elsewhere.
		return X509CertificateLoader.LoadPkcs12(certificateWithKey.Export(X509ContentType.Pkcs12), password: null);
	}

	/// <summary>
	/// Validates the server certificate against the realm's root CA as the only trusted root.
	/// </summary>
	private static bool ChainsToTrustBundle(X509Certificate? serverCertificate, X509Chain? chain, SslPolicyErrors errors, X509Certificate2 trustBundle)
	{
		Console.WriteLine($"[mTLS] validating Tidecloak server certificate - SslPolicyErrors: {errors}");
		Console.WriteLine($"[mTLS]   trust bundle: subject='{trustBundle.Subject}' issuer='{trustBundle.Issuer}' thumbprint={trustBundle.Thumbprint} notBefore={trustBundle.NotBefore:O} notAfter={trustBundle.NotAfter:O}");

		if (serverCertificate == null)
		{
			Console.WriteLine("[mTLS]   REJECTED: the server presented no certificate");
			return false;
		}

		using var serverCertificateCopy = X509CertificateLoader.LoadCertificate(serverCertificate.Export(X509ContentType.Cert));
		Console.WriteLine($"[mTLS]   server cert: subject='{serverCertificateCopy.Subject}' issuer='{serverCertificateCopy.Issuer}' thumbprint={serverCertificateCopy.Thumbprint} notBefore={serverCertificateCopy.NotBefore:O} notAfter={serverCertificateCopy.NotAfter:O}");
		// the SANs are what the hostname is matched against - a bare CN is ignored by modern validation
		var subjectAlternativeNames = serverCertificateCopy.Extensions
			.OfType<X509SubjectAlternativeNameExtension>()
			.SelectMany(extension => extension.EnumerateDnsNames().Concat(extension.EnumerateIPAddresses().Select(ip => ip.ToString())))
			.ToArray();
		Console.WriteLine($"[mTLS]   server cert SANs: {(subjectAlternativeNames.Length == 0 ? "(none - hostname validation cannot pass)" : string.Join(", ", subjectAlternativeNames))}");

		if (chain != null)
		{
			Console.WriteLine($"[mTLS]   chain the server sent ({chain.ChainElements.Count} element(s)):");
			foreach (var element in chain.ChainElements)
			{
				var elementStatus = element.ChainElementStatus.Length == 0 ? "ok" : string.Join(", ", element.ChainElementStatus.Select(s => $"{s.Status}: {s.StatusInformation?.Trim()}"));
				Console.WriteLine($"[mTLS]     subject='{element.Certificate.Subject}' issuer='{element.Certificate.Issuer}' thumbprint={element.Certificate.Thumbprint} status={elementStatus}");
			}
		}
		else
		{
			Console.WriteLine("[mTLS]   the server sent no chain - if its certificate is not directly signed by the trust bundle root, no intermediate is available to bridge the gap");
		}

		// the trust bundle only answers "who signed this" - a wrong hostname is still a failure
		if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable) || errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
		{
			Console.WriteLine($"[mTLS]   REJECTED before chain building: {errors}. A name mismatch means the host in the request url is not covered by the SANs above.");
			return false;
		}

		using var trustBundleChain = new X509Chain();
		trustBundleChain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
		trustBundleChain.ChainPolicy.CustomTrustStore.Add(trustBundle);
		// the realm CA publishes no CRL/OCSP endpoint, so a revocation check would only ever fail as unknown
		trustBundleChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

		// reuse any intermediates the server sent alongside its certificate
		if (chain != null)
		{
			foreach (var element in chain.ChainElements)
			{
				trustBundleChain.ChainPolicy.ExtraStore.Add(element.Certificate);
			}
		}

		var built = trustBundleChain.Build(serverCertificateCopy);
		if (built)
		{
			Console.WriteLine("[mTLS]   ACCEPTED: server certificate chains to the realm trust bundle");
			return true;
		}

		Console.WriteLine("[mTLS]   REJECTED: server certificate does not chain to the realm trust bundle");
		foreach (var status in trustBundleChain.ChainStatus)
		{
			Console.WriteLine($"[mTLS]     chain status: {status.Status} - {status.StatusInformation?.Trim()}");
		}
		foreach (var element in trustBundleChain.ChainElements)
		{
			var elementStatus = element.ChainElementStatus.Length == 0 ? "ok" : string.Join(", ", element.ChainElementStatus.Select(s => $"{s.Status}: {s.StatusInformation?.Trim()}"));
			Console.WriteLine($"[mTLS]     built element: subject='{element.Certificate.Subject}' issuer='{element.Certificate.Issuer}' status={elementStatus}");
		}
		return false;
	}

	private static string GetClientId(IConfigurationSection section)
		=> section["resource"] ?? throw new InvalidOperationException("Missing required configuration: resource");

	private static string GetRealm(IConfigurationSection section)
		=> section["realm"] ?? throw new InvalidOperationException("Missing required configuration: realm");

	private static string GetBaseRealmUrl(IConfigurationSection section)
	{
		string realm = GetRealm(section);
		string tidecloakDomain = section["auth-server-url"] ?? throw new InvalidOperationException("Missing required configuration: auth-server-url");

		return new Uri(tidecloakDomain).GetLeftPart(UriPartial.Authority) + $"/realms/{realm}/";
	}

	private static ResourceIdentityPaths GetResourceIdentityPaths(IConfigurationSection section) => new(
		section["certificate_path"] ?? Utils.RESOURCE_CERTIFICATE_DEFAULT_PATH,
		section["root_ca_path"] ?? Utils.ROOT_CA_DEFAULT_PATH,
		section["certificate_request_path"] ?? Utils.RESOURCE_CERTIFICATE_REQUEST_DEFAULT_PATH);

	/// <summary>Where the enrolled resource identity lives on disk. The resource key is not here - that belongs to the <see cref="IResourceKeyProvider"/>.</summary>
	private sealed record ResourceIdentityPaths(string CertificatePath, string RootCaPath, string CertificateRequestPath);

	/// <summary>A resource's mTLS credentials: its signed certificate, and the realm root CA that signed it.</summary>
	private sealed record ResourceIdentity(X509Certificate2 Certificate, X509Certificate2 TrustBundle);

	private const string ACTIVE_RESOURCE_IDENTITY_STATUS = "ACTIVE";

	private sealed class ResourceIdentityResponse
	{
		public string status { get; set; } = string.Empty;
		public string? certificate { get; set; }
		public string? rootCa { get; set; }
		public string GetCertificate()
		{
			if (status != "ACTIVE") throw new InvalidOperationException("Certificate is not active");
			if (certificate == null) throw new InvalidOperationException("Certificate is null");
			return certificate;
		}
		public string GetRootCa()
		{
			if (status != "ACTIVE") throw new InvalidOperationException("Certificate is not active");
			if (rootCa == null) throw new InvalidOperationException("Root CA is null");
			return rootCa;
		}
	}
}
