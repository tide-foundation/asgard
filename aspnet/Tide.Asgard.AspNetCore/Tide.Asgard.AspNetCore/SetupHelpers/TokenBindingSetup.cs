// Copyright (c) Okta, Inc. and/or its affiliates. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE-APACHE-2.0 in the project root.
// Modifications Copyright (c) Tide Foundation Limited.

using Keycloak.AuthServices.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Text;
using Tide.Asgard.AspNetCore.Authentication.mTLS;
using Tide.Asgard.AspNetCore.DPoP;
using Tide.Asgard.AspNetCore.DPoP.EventHandlers;

namespace Tide.Asgard.AspNetCore.Authentication.SetupHelpers;

public static class TokenBindingSetup
{
	public static AsgardWebApiConfigurationBuilder RequireTokenBinding(this AsgardWebApiConfigurationBuilder asgardBuilder, Action<BindingOptions> bindingOptions)
	{
		var options = new BindingOptions();
		bindingOptions.Invoke(options);

		bool bindingAdded = false;

		if (options.SupportBindings.HasFlag(Binding.DPoP))
		{
			asgardBuilder.KeycloakBuilder.SupportDPoPBoundTokens();
			bindingAdded = true;
		}

		if (options.SupportBindings.HasFlag(Binding.mTLS))
		{
			asgardBuilder.KeycloakBuilder.SupportMTLSBoundTokens(asgardBuilder.WebBuilder.WebHost);
			bindingAdded = true;
		}

		if (bindingAdded)
		{
			// Configure validator chain for requests
			asgardBuilder.WebBuilder.Services.AddSingleton<IPostConfigureOptions<JwtBearerOptions>>(
				new AsgardJwtBearerPostConfigureOptions(asgardBuilder.KeycloakBuilder.JwtBearerAuthenticationScheme, options.SupportBindings));

			// Configure all the exchange + tide stuff
			asgardBuilder.WebBuilder.Services.AddTideExtensions(asgardBuilder.WebBuilder.Configuration);
		}

		return asgardBuilder;
	}
	private static KeycloakWebApiAuthenticationBuilder SupportDPoPBoundTokens(
		this KeycloakWebApiAuthenticationBuilder builder,
		Action<DPoPOptions> configureDPoPOptions)
	{
		ArgumentNullException.ThrowIfNull(builder);
		ArgumentNullException.ThrowIfNull(configureDPoPOptions);

		var dPoPOptions = new DPoPOptions();
		configureDPoPOptions(dPoPOptions);

		builder.Services.TryAddSingleton(dPoPOptions);
		builder.Services.TryAddScoped<IDPoPProofValidationService, DPoPProofValidationService>();
		builder.Services.TryAddScoped<MessageReceivedHandler>();
		builder.Services.TryAddScoped<TokenValidationHandler>();
		builder.Services.TryAddScoped<ChallengeHandler>();

		return builder;
	}
	private static KeycloakWebApiAuthenticationBuilder SupportMTLSBoundTokens(
		this KeycloakWebApiAuthenticationBuilder builder,
		IWebHostBuilder hostBuilder,
		Action<HttpsConnectionAdapterOptions>? configureOptions = null)
	{
		ArgumentNullException.ThrowIfNull(builder);
		ArgumentNullException.ThrowIfNull(hostBuilder);

		hostBuilder.ConfigureKestrel(kestrel => kestrel.ConfigureHttpsDefaults(https =>
		{
			// filled in once enrollment is approved, so both callbacks read it per connection rather than now
			var register = kestrel.ApplicationServices.GetService<CertificateRegisterSingleton>()
				?? throw new InvalidOperationException($"{nameof(SupportMTLSBoundTokens)} needs AddAsgard, which enrolls the certificate the https endpoints present.");
			var logger = kestrel.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(RealmCertificateValidation));

			// present this resource's enrolled certificate
			https.ServerCertificateSelector = (_, _) => register.Current?.ClientCertificate;

			// only a caller with a client certificate from the realm CA gets through the handshake
			https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
			https.ClientCertificateValidation = (certificate, _, _) =>
				register.Current is { } credentials && RealmCertificateValidation.ValidateResourceClientCertificate(certificate, credentials, logger);
			// the TLS stack checks the client certificate itself before the validation above runs - with revocation checks
			// on, as Kestrel defaults to - so that check is taken off the network as well
			https.OnAuthenticate = (_, ssl) => ssl.CertificateChainPolicy = RealmCertificateValidation.CreateHandshakeChainPolicy();

			configureOptions?.Invoke(https);
		}));

		return builder;
	}
	/// <summary>
	///     Enables DPoP (Demonstration of Proof-of-Possession) support for the Asgard API authentication builder.
	/// </summary>
	/// <param name="builder">
	///     The <see cref="KeycloakWebApiAuthenticationBuilder" /> instance to configure.
	/// </param>
	/// <returns>
	///     The configured <see cref="KeycloakWebApiAuthenticationBuilder" /> instance.
	/// </returns>
	private static KeycloakWebApiAuthenticationBuilder SupportDPoPBoundTokens(
		this KeycloakWebApiAuthenticationBuilder builder)
	{
		return builder.SupportDPoPBoundTokens(_ => { });
	}
}

public class BindingOptions
{
	/// <summary>
	/// Defaults to DPoP
	/// </summary>
	public Binding SupportBindings { get; set; } = Binding.DPoP;
}
[Flags ]
public enum Binding
{
	/// <summary>
	/// Will allow your server to accept DPoP bound tokens
	/// </summary>
	DPoP = 1,
	/// <summary>
	/// Will allow your server to accept mTLS client certifications automatically.
	/// </summary>
	mTLS = 2
}
