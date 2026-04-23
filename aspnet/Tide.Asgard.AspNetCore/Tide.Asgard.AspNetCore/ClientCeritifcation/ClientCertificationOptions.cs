using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Tide.Asgard.AspNetCore.Authentication.ClientCertification;

public class ClientCertificationOptions
{
	public const string CredentialFileName = "client.pfx";

	public required ConfidentialClientType ClientType { get; set; }
	public required string CredentialPath { get; set; }
	public required string ClientId { get; set; }
	public required string AuthorizationServerHost { get; set; }

	private int _registrationStatus = (int)RegistrationStatus.Unknown;
	public RegistrationStatus RegistrationStatus
	{
		get => (RegistrationStatus)Volatile.Read(ref _registrationStatus);
		set => Interlocked.Exchange(ref _registrationStatus, (int)value);
	}

	public bool TrySwitchStatus(RegistrationStatus expected, RegistrationStatus next) =>
		Interlocked.CompareExchange(ref _registrationStatus, (int)next, (int)expected) == (int)expected;
}

public enum RegistrationStatus
{
	Unknown,
	Unregistered,
	Certifying,
	Registered,
	Error
}