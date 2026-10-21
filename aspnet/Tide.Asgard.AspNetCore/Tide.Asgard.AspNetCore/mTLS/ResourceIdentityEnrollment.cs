// Copyright (c) Okta, Inc. and/or its affiliates. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE-APACHE-2.0 in the project root.
// Modifications Copyright (c) Tide Foundation Limited.

using Cryptide.Key;
using Cryptide.Tools;
using Microsoft.Extensions.Configuration;
using Ork.Clients.Providers;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Tide.Asgard.Core.Crypto.Ed25519;
using static Tide.Asgard.AspNetCore.Authentication.SetupHelpers.TidecloakAdapter;

namespace Tide.Asgard.AspNetCore.Authentication.mTLS;

/// <summary>Where the enrolled resource identity lives on disk. The resource key is not here - that belongs to the <see cref="IResourceKeyProvider"/>.</summary>
internal sealed record ResourceIdentityPaths(string CertificatePath, string RootCaPath, string CertificateRequestPath);
internal sealed record ResourceIdentity(X509Certificate2 Certificate, X509Certificate2 TrustBundle);

/// <summary>
/// Gets this resource its mTLS identity: a certificate for its resource key, signed by the realm CA once an admin approves it.
/// </summary>
internal static class ResourceIdentityEnrollment
{
	/// <summary>
	/// One enrollment attempt. Publishes the mTLS credentials to <paramref name="register"/> and returns true once
	/// Tidecloak has an approved certificate for this resource key; false while the request is still pending.
	/// </summary>
	internal static async Task<bool> TryRegisterResourceIdentity(IConfigurationSection configurationSection, IResourceKeyProvider resourceKeyProvider, CertificateRegisterSingleton register, ResourceIdentityPaths paths)
	{
		var identity = await EnrollResourceIdentity(
			configurationSection,
			GetBaseRealmUrl(configurationSection),
			paths,
			resourceKeyProvider);

		if (identity == null) return false;

		register.Register(CreateClientCertificate(identity.Certificate, resourceKeyProvider.GetResourceKey().IdentitySRK), identity.TrustBundle);
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
		var (identityKey, tideKey) = GetOrCreateResourceKey(resourceKeyProvider);

		// enrolled on an earlier run - nothing to do
		var existingIdentity = LoadResourceIdentity(configurationSection, paths, identityKey);
		if (existingIdentity != null) return existingIdentity;

		using var httpClient = new HttpClient();

		// nothing requested yet -> submit a signing request for the resource key
		bool justCreated = false;
		if (!File.Exists(paths.CertificateRequestPath))
		{
			var enrollmentToken = configurationSection["enrollment_token"] ?? throw new InvalidOperationException("'enrollment_token' required in configuration");

			using var signingKey = ToECDsa(identityKey);
			var certificateRequest = new CertificateRequest(new X500DistinguishedName($"CN=client_{GetClientId(configurationSection)}"), signingKey, HashAlgorithmName.SHA256);

			// add tide key as subjectAltPublicKeyInfo
			certificateRequest.CertificateExtensions.Add(new X509Extension(new Oid("2.5.29.72"), tideKey.ToSubjectPublicKeyInfoBytes(), critical: false)); // OID for subjectAltPublicKeyInfo

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
		var statusResponse = await httpClient.GetAsync(baseRealmUrl + $"tide-server-identity/status?fingerprint={Uri.EscapeDataString(GetResourceKeyFingerprint(identityKey))}");
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
		VerifyResourceIdentity(certificate, trustBundle, configurationSection, identityKey);

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
	/// The resource key, minting and persisting one on first run. The provider is the single source of truth for it -
	/// the certificate and trust bundle on disk are only ever read back against whatever key it holds.
	/// </summary>
	private static (TideKey IdentitySRK, TideKey TideSRK) GetOrCreateResourceKey(IResourceKeyProvider resourceKeyProvider)
	{
		try
		{
			return resourceKeyProvider.GetResourceKey();
		}
		catch (FileNotFoundException)
		{
			var identityKey = TideKey.NewKey(TideComponentSchemeType.P256);
			var tideKey = TideKey.NewKey(TideComponentSchemeType.Ed25519);
			resourceKeyProvider.SetResourceKey(identityKey, tideKey);
			return (identityKey, tideKey);
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
