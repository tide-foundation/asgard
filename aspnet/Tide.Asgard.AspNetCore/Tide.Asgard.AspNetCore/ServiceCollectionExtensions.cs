// Copyright (c) Okta, Inc. and/or its affiliates. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE-APACHE-2.0 in the project root.
// Modifications Copyright (c) Tide Foundation Limited.

using Cryptide.Hashing;
using Cryptide.Key;
using Cryptide.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
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
using Tide.Asgard.AspNetCore.Authentication.Middleware;
using Tide.Asgard.AspNetCore.Authentication.mTLS;
using Tide.Asgard.AspNetCore.Authentication.TokenExchange;
using Tide.Asgard.Core;
using Tide.Asgard.Core.Crypto.Ed25519;
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

		// the register is the http client's only source of mTLS credentials. It is created here so the same instance
		// can be filled in now if the identity is ready, or later by the background enrollment service if it is not.
		var register = new CertificateRegisterSingleton();
		services.AddSingleton(register);

		switch (authMode)
		{
			case ResourceAuthenticationMode.AutoMTLSEnrollment:
				try
				{
					await TryRegisterResourceIdentity(configurationSection, resourceKeyProvider, register);
				}
				catch (Exception exception)
				{
					// Tidecloak being unreachable at startup is not fatal in this mode - the service retries
					Console.WriteLine($"[mTLS] enrollment attempt failed at startup: {exception.Message}");
				}

				// keeps trying every minute, so an approval that lands after startup does not need a restart
				services.AddHostedService(serviceProvider => new ResourceIdentityEnrollmentService(
					configurationSection,
					serviceProvider.GetRequiredService<IResourceKeyProvider>(),
					register,
					serviceProvider.GetRequiredService<ILogger<ResourceIdentityEnrollmentService>>()));
				break;

			case ResourceAuthenticationMode.MTLS:
				// no enrollment in this mode - the credentials have to be on disk already, so a missing identity is a
				// deployment error rather than something to wait for
				var identity = LoadResourceIdentity(configurationSection, paths, resourceKeyProvider.GetResourceKey())
					?? throw new InvalidOperationException($"{ResourceAuthenticationMode.MTLS} requires a signed certificate at '{paths.CertificatePath}' and a root CA at '{paths.RootCaPath}'. Use {ResourceAuthenticationMode.AutoMTLSEnrollment} to enroll them.");
				register.Register(CreateClientCertificate(identity.Certificate, resourceKeyProvider.GetResourceKey()), identity.TrustBundle);
				break;
			default:
				throw new NotSupportedException($"Unsupported {nameof(ResourceAuthenticationMode)}: {authMode}");
		}

		ConfigureMTLSTidecloakClient(services, baseRealmUrl, realm);
	}

	/// <summary>
	/// One enrollment attempt. Publishes the mTLS credentials to <paramref name="register"/> and returns true once
	/// Tidecloak has an approved certificate for this resource key; false while the request is still pending.
	/// </summary>
	internal static async Task<bool> TryRegisterResourceIdentity(IConfigurationSection configurationSection, IResourceKeyProvider resourceKeyProvider, CertificateRegisterSingleton register)
	{
		var identity = await EnrollResourceIdentity(
			configurationSection,
			GetBaseRealmUrl(configurationSection),
			GetResourceIdentityPaths(configurationSection),
			resourceKeyProvider);

		if (identity == null) return false;

		register.Register(CreateClientCertificate(identity.Certificate, resourceKeyProvider.GetResourceKey()), identity.TrustBundle);
		return true;
	}

	/// <summary>
	/// Brings the resource identity to a usable state: mints the resource key if there isn't one, requests a
	/// certificate with the enrollment token if none has been requested yet, and collects the signed certificate once
	/// Tidecloak has approved it. Safe to call on every startup - each step is skipped once its output is on disk -
	/// and safe to call repeatedly. Null means the request is still waiting on approval.
	/// </summary>
	private static async Task<ResourceIdentity?> EnrollResourceIdentity(IConfigurationSection configurationSection, string baseRealmUrl, ResourceIdentityPaths paths, IResourceKeyProvider resourceKeyProvider)
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
			if (justCreated) Console.WriteLine("Certificate request submitted to Tidecloak. Approve it in Tidecloak to finish enrollment.");
			return null;
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
	private static void ConfigureMTLSTidecloakClient(IServiceCollection services, string baseRealmUrl, string realm)
	{
		// The reverse proxy in front of Tidecloak serves each realm on its own "{realm}.client.{domain}" vhost and
		// selects that realm's certificate from the server name.
		var configuredUri = new Uri(baseRealmUrl);
		var realmUri = new UriBuilder(configuredUri)
		{
			Scheme = Uri.UriSchemeHttps,
			Host = $"{realm}.client.{configuredUri.Host}",
			Port = 8443
		}.Uri;

		services.AddHttpClient("Tidecloak", client =>
		{
			client.BaseAddress = realmUri;
		})
		.AddHttpMessageHandler(serviceProvider => new ResourceIdentityRequiredHandler(
			serviceProvider.GetRequiredService<CertificateRegisterSingleton>(),
			serviceProvider.GetRequiredService<ILogger<ResourceIdentityRequiredHandler>>()))
		.ConfigurePrimaryHttpMessageHandler(serviceProvider =>
		{
			var register = serviceProvider.GetRequiredService<CertificateRegisterSingleton>();
			return new SocketsHttpHandler
			{
				SslOptions = new SslClientAuthenticationOptions
				{
					LocalCertificateSelectionCallback = (_, _, _, _, _) => register.Current?.ClientCertificate!,
					RemoteCertificateValidationCallback = (_, serverCertificate, chain, errors) =>
						register.Current is { } credentials && ValidateTidecloakCertificate(serverCertificate, chain, errors, credentials)
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
	private static bool ValidateTidecloakCertificate(X509Certificate? serverCertificate, X509Chain? chain, SslPolicyErrors errors, CertificateRegisterSingleton.ResourceCredentials credentials)
	{
		var (clientCertificate, trustBundle) = credentials;

		// a handshake that works is not worth a word - the diagnostics are collected as the checks run and only
		// printed by Reject, so the happy path stays silent
		var diagnostics = new List<string>();

		bool Reject(string reason)
		{
			Console.WriteLine($"[mTLS] REJECTED the Tidecloak server certificate: {reason}");
			Console.WriteLine($"[mTLS]   SslPolicyErrors: {errors}");
			Console.WriteLine($"[mTLS]   client cert presented: subject='{clientCertificate.Subject}' thumbprint={clientCertificate.Thumbprint} hasPrivateKey={clientCertificate.HasPrivateKey}");
			Console.WriteLine($"[mTLS]   trust bundle: subject='{trustBundle.Subject}' issuer='{trustBundle.Issuer}' thumbprint={trustBundle.Thumbprint} notBefore={trustBundle.NotBefore:O} notAfter={trustBundle.NotAfter:O}");
			foreach (var diagnostic in diagnostics)
			{
				Console.WriteLine($"[mTLS]   {diagnostic}");
			}
			return false;
		}

		if (serverCertificate == null) return Reject("the server presented no certificate");

		using var serverCertificateCopy = X509CertificateLoader.LoadCertificate(serverCertificate.Export(X509ContentType.Cert));
		diagnostics.Add($"server cert: subject='{serverCertificateCopy.Subject}' issuer='{serverCertificateCopy.Issuer}' thumbprint={serverCertificateCopy.Thumbprint} notBefore={serverCertificateCopy.NotBefore:O} notAfter={serverCertificateCopy.NotAfter:O}");

		// the SANs are what the hostname is matched against - a bare CN is ignored by modern validation
		var subjectAlternativeNames = serverCertificateCopy.Extensions
			.OfType<X509SubjectAlternativeNameExtension>()
			.SelectMany(extension => extension.EnumerateDnsNames().Concat(extension.EnumerateIPAddresses().Select(ip => ip.ToString())))
			.ToArray();
		diagnostics.Add($"server cert SANs: {(subjectAlternativeNames.Length == 0 ? "(none - hostname validation cannot pass)" : string.Join(", ", subjectAlternativeNames))}");

		if (chain != null)
		{
			diagnostics.Add($"the server sent a {chain.ChainElements.Count} element chain:");
			foreach (var element in chain.ChainElements)
			{
				diagnostics.Add($"  subject='{element.Certificate.Subject}' issuer='{element.Certificate.Issuer}' thumbprint={element.Certificate.Thumbprint} status={DescribeStatus(element.ChainElementStatus)}");
			}
		}
		else
		{
			diagnostics.Add("the server sent no chain - if its certificate is not directly signed by the trust bundle root, no intermediate is available to bridge the gap");
		}

		// the trust bundle only answers "who signed this" - a wrong hostname is still a failure
		if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable) || errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
			return Reject("the host in the request url is not covered by its SANs");

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

		if (trustBundleChain.Build(serverCertificateCopy)) return true;

		foreach (var status in trustBundleChain.ChainStatus)
		{
			diagnostics.Add($"chain status: {status.Status} - {status.StatusInformation?.Trim()}");
		}
		foreach (var element in trustBundleChain.ChainElements)
		{
			diagnostics.Add($"built element: subject='{element.Certificate.Subject}' issuer='{element.Certificate.Issuer}' status={DescribeStatus(element.ChainElementStatus)}");
		}
		return Reject("it does not chain to the realm trust bundle");
	}

	/// <summary>The per-element chain status, flattened for one diagnostic line.</summary>
	private static string DescribeStatus(X509ChainStatus[] chainElementStatus)
		=> chainElementStatus.Length == 0
			? "ok"
			: string.Join(", ", chainElementStatus.Select(status => $"{status.Status}: {status.StatusInformation?.Trim()}"));

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
