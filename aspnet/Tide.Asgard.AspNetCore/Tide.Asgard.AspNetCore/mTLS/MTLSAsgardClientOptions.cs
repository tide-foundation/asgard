using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ork.Clients.Providers;
using System.Net.Security;

namespace Tide.Asgard.AspNetCore.Authentication.mTLS;

/// <summary>
/// Configures the http client for each resource this one calls over mutual TLS, named "Asgard:{clientId}". Each resource
/// gets a client of its own because the identity a server certificate is checked against belongs to the connections,
/// not to a request: a pooled connection is reused for later requests to the same host without its certificate being
/// checked again. Any such name is configured the first time it is asked for, so no resource is registered up front.
/// </summary>
internal sealed class MTLSAsgardClientOptions : IConfigureNamedOptions<HttpClientFactoryOptions>
{
	public const string NAME_PREFIX = "Asgardv1";

	public void Configure(string? name, HttpClientFactoryOptions options)
	{
		if (name is null || !name.StartsWith(NAME_PREFIX, StringComparison.Ordinal)) return;
		var clientId = name.Split(':')[^1];

		options.HttpMessageHandlerBuilderActions.Add(builder =>
		{
			var register = builder.Services.GetRequiredService<CertificateRegisterSingleton>();
			var httpContextAccessor = builder.Services.GetRequiredService<IHttpContextAccessor>();
			var validationLogger = builder.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(RealmCertificateValidation));

			// outermost first: Asgard-* headers out and Asgard errors back, then nothing leaves without mTLS
			builder.AdditionalHandlers.Add(builder.Services.GetRequiredService<AsgardMessageHandler>());
			builder.AdditionalHandlers.Add(new ClientMTLSAndExchangeHandler(clientId, httpContextAccessor, builder.Services.GetRequiredService<IResourceKeyProvider>(), register, builder.Services.GetRequiredService<ILogger<ClientMTLSAndExchangeHandler>>()));

			builder.PrimaryHandler = new SocketsHttpHandler
			{
				SslOptions = new SslClientAuthenticationOptions
				{
					// both read the register per connection, so a resource that enrolls after startup presents its
					// certificate from then on
					LocalCertificateSelectionCallback = (_, _, _, _, _) => register.Current?.ClientCertificate!,
					RemoteCertificateValidationCallback = (_, serverCertificate, _, _) =>
						register.Current is { } credentials && RealmCertificateValidation.ValidateResourceServerCertificate(serverCertificate, credentials, clientId, validationLogger),
					CertificateChainPolicy = RealmCertificateValidation.CreateHandshakeChainPolicy()
				}
			};
		});
	}

	public void Configure(HttpClientFactoryOptions options) => Configure(Options.DefaultName, options);
}
