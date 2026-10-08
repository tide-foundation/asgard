using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ork.Clients.Providers;
using System.Globalization;

namespace Tide.Asgard.AspNetCore.Authentication.mTLS;

/// <summary>
/// Finishes the resource identity enrollment in the background. A certificate request has to be approved by a human
/// in Tidecloak, so the resource would otherwise have to be started, stopped once the approval lands, and started
/// again. Instead the server comes up without credentials - the "Tidecloak" client rejects every connection while
/// that is the case - and this service keeps asking until the signed certificate is collectable, at which point it
/// publishes the credentials to <see cref="CertificateRegisterSingleton"/> and stops.
/// </summary>
public sealed class ResourceIdentityEnrollmentService(
	IConfigurationSection configurationSection,
	IResourceKeyProvider resourceKeyProvider,
	CertificateRegisterSingleton register,
	ILogger<ResourceIdentityEnrollmentService> logger) : BackgroundService
{
	/// <summary>
	/// How long to wait between enrollment attempts. Read from 'enrollment_poll_interval_seconds' in the Keycloak
	/// configuration section, defaulting to <see cref="Utils.ENROLLMENT_POLL_INTERVAL_DEFAULT_SECONDS"/>.
	/// </summary>
	private readonly TimeSpan PollInterval = GetPollInterval(configurationSection);

	private static TimeSpan GetPollInterval(IConfigurationSection section)
	{
		var configured = section["enrollment_poll_interval_seconds"];
		if (configured == null) return TimeSpan.FromSeconds(Utils.ENROLLMENT_POLL_INTERVAL_DEFAULT_SECONDS);

		if (!double.TryParse(configured, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0)
			throw new InvalidOperationException($"'enrollment_poll_interval_seconds' must be a positive number of seconds, but was '{configured}'");

		return TimeSpan.FromSeconds(seconds);
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		// startup already collected the identity - there is nothing left to wait for
		if (register.IsRegistered) return;

		logger.LogWarning(
			"The resource identity is not enrolled yet, so calls to Tidecloak will fail. Approve the certificate request in Tidecloak - this will be retried every {PollInterval}.",
			PollInterval);

		using var timer = new PeriodicTimer(PollInterval);

		try
		{
			while (!register.IsRegistered && await timer.WaitForNextTickAsync(stoppingToken))
			{
				try
				{
					if (await ServiceCollectionExtensions.TryRegisterResourceIdentity(configurationSection, resourceKeyProvider, register))
					{
						logger.LogInformation("Resource identity enrolled. The Tidecloak mTLS client is now usable.");
						return;
					}

					logger.LogWarning("The certificate request is still awaiting approval in Tidecloak.");
				}
				catch (Exception exception)
				{
					logger.LogWarning(exception, "Resource identity enrollment attempt failed. Retrying in {PollInterval}.", PollInterval);
				}
			}
		}
		catch (OperationCanceledException)
		{
			// the host is shutting down before the identity was ever approved
		}
	}
}
