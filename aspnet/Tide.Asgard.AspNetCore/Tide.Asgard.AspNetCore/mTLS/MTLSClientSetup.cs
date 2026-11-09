// Copyright (c) Okta, Inc. and/or its affiliates. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE-APACHE-2.0 in the project root.
// Modifications Copyright (c) Tide Foundation Limited.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ork.Clients.Providers;
using System.Net.Security;
using static Tide.Asgard.AspNetCore.Authentication.mTLS.RealmCertificateValidation;

namespace Tide.Asgard.AspNetCore.Authentication.mTLS;

/// <summary>
/// Registers this resource's mTLS identity and the http clients that use it.
/// </summary>
internal static class MTLSClientSetup
{
	/// <summary>
	/// Registers this resource's mTLS credentials: enrolls them now if Tidecloak has already approved the certificate,
	/// and keeps trying in the background if it has not.
	/// </summary>
	internal static async Task AddResourceIdentity(IServiceCollection services, IConfigurationSection configurationSection, IResourceKeyProvider resourceKeyProvider, ResourceIdentityPaths identityPaths)
	{
		// the register is the http client's only source of mTLS credentials. It is created here so the same instance
		// can be filled in now if the identity is ready, or later by the background enrollment service if it is not.
		var register = new CertificateRegisterSingleton();
		services.AddSingleton(register);

		try
		{
			await ResourceIdentityEnrollment.TryRegisterResourceIdentity(configurationSection, resourceKeyProvider, register, identityPaths);
		}
		catch (Exception exception)
		{
			// Tidecloak being unreachable at startup is not fatal - the service retries
			Console.WriteLine($"[mTLS] enrollment attempt failed at startup: {exception.Message}");
		}

		// keeps trying on the configured interval, so an approval that lands after startup does not need a restart
		services.AddHostedService(serviceProvider => new ResourceIdentityEnrollmentService(
			configurationSection,
			serviceProvider.GetRequiredService<IResourceKeyProvider>(),
			register,
			identityPaths,
			serviceProvider.GetRequiredService<ILogger<ResourceIdentityEnrollmentService>>())
			);
	}

	/// <summary>
	/// Registers the "Tidecloak" http client for mutual TLS. The resource's signed certificate is presented as the
	/// client certificate, and Tidecloak's server certificate has to chain to the realm's root CA rather than to
	/// whatever the machine trust store happens to contain.
	/// </summary>
	internal static void AddTidecloakClient(IServiceCollection services, string baseRealmUrl, string realm)
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
			var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(RealmCertificateValidation));
			return new SocketsHttpHandler
			{
				SslOptions = new SslClientAuthenticationOptions
				{
					LocalCertificateSelectionCallback = (_, _, _, _, _) => register.Current?.ClientCertificate!,
					RemoteCertificateValidationCallback = (_, serverCertificate, _, errors) =>
						register.Current is { } credentials && ValidateTidecloakCertificate(serverCertificate, errors, credentials, realm, logger),
					CertificateChainPolicy = CreateHandshakeChainPolicy()
				}
			};
		});
	}

	/// <summary>
	/// Registers the "Asgard:{clientId}" http clients, which call other resources over mutual TLS. Each is configured by
	/// <see cref="MTLSAsgardClientOptions"/> the first time it is asked for.
	/// </summary>
	internal static void AddResourceClients(IServiceCollection services)
	{
		services.AddHttpClient(); // the factory the clients come from
		services.AddTransient<AsgardMessageHandler>();
		services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<HttpClientFactoryOptions>, MTLSAsgardClientOptions>());
	}
}
