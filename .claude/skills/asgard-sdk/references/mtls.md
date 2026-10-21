# mTLS: enrollment, certificates, and talking to other resources

## Enrollment

1. On first start Asgard makes two keys: `resource.key` (P-256, the TLS identity) and `tide.key` (Ed25519, carried in
   the certificate request as extension `2.5.29.72`).
2. It sends a certificate request to TideCloak with the adapter's one-time `enrollment_token`, **then** writes
   `resource.csr`. If that write fails (e.g. the folder isn't writable) the token is already used up - fix the path and
   download a fresh adapter config.
3. It asks every 60 seconds whether an admin has approved it (`enrollment_poll_interval_seconds`, read from the
   `Keycloak` section). Until then, anything that needs TideCloak answers `503 ResourceIdentityNotRegistered`.
4. Once approved it saves `resource.crt` and `root.crt`, checks the root's key is the realm's Tide key, and starts using
   them - no restart needed.

The code: `mTLS/ResourceIdentityEnrollment.cs` (one attempt), `mTLS/ResourceIdentityEnrollmentService.cs` (the
background retries) and `mTLS/MTLSClientSetup.cs` (registration). It all starts from `RequireTokenBinding`.

Where the files go - all in the **`Asgard`** config section (the `Keycloak` section is only the downloaded adapter):
`identity_key_path`, `tide_key_path`, `certificate_request_path`, `certificate_path`, `root_ca_path`. Defaults are
`./resource.key` etc., relative to the working directory. In Docker set them as `Asgard__certificate_path=...`.

## Talking to TideCloak

The named HttpClient `"Tidecloak"` calls `https://{realm}.client.{auth-server-url host}:8443/realms/{realm}/` with the
backend's certificate, and trusts only the realm root CA. For a local TideCloak, `{realm}.client.localhost` needs a
`/etc/hosts` entry.

## The certificates

All are signed by the ORK cohort (see `ResourceIdentitySignRequest.cs` in `~/project/ork` if the user points you there).

| Certificate | Subject | Usage | SAN |
|---|---|---|---|
| Realm root CA (`root.crt`) | `CN=realm_{realm}_ca`, Ed25519, `pathlen:0` | signs certificates | none |
| TideCloak's mTLS server | `CN=realm_{realm}` | server auth | `DNS:{realm}.client.{host}` |
| A resource (`resource.crt`) | `CN=client_{clientId}` - exactly that, nothing else | server + client auth | `URI:urn:tide:client:{client uuid}` |

Resource certificates have **no DNS name**, so you can't check them by hostname - check the subject instead. The root is
`pathlen:0`, so there are never intermediate CAs. The root signs with Ed25519, which .NET's chain building handles on
Linux (OpenSSL) but likely not on Windows.

## Checking certificates

All three live in `mTLS/RealmCertificateValidation.cs`:

- `ValidateTidecloakCertificate(certificate, errors, credentials, realm, logger)` - TideCloak's server certificate: chain
  to the realm root, server-auth usage, subject exactly `CN=realm_{realm}`, **and** hostname match. The subject is what
  tells it apart from a resource certificate, which also carries server auth.
- `ValidateResourceServerCertificate(certificate, credentials, clientId, logger)` - another resource's server
  certificate: chain to the realm root, server-auth usage, and subject exactly `CN=client_{clientId}`. It ignores the
  hostname mismatch every resource certificate produces. A client id must match `[A-Za-z0-9_-]{1,60}`
  (`Utils.IsCertifiableClientId`).
- `ValidateResourceClientCertificate` - a calling resource's client certificate: chain to the realm root, client-auth
  usage. Which resource it is gets decided by the certificate-bound token, not here.

Chain building never goes to the network (`DisableCertificateDownloads`, no revocation). That covers the TLS stack's own
check that runs before these callbacks: `CreateHandshakeChainPolicy` is set on both http clients' `SslOptions` and, through
`OnAuthenticate`, on Kestrel. Otherwise a peer's certificate can carry a url this resource then fetches, before any
authentication, holding the handshake ~15s if it never answers. Rejections are logged through `ILogger` by thumbprint, never
by the subject of a certificate that hasn't chained yet. The inbound one is `Debug`, because anyone can trigger it.

## Calling another resource

```csharp
[RequireExchangeApproval(ApprovalRequirement.TideEnabled, "analytics")] // the exchange target is the called resource
public async Task<IActionResult> Summary()
{
	var client = asgard.GetHttpClient("analytics"); // the called resource's client id
	var response = await client.GetAsync("https://analytics.internal/api/summary");
	// ...
}
```

- Each id gets its own named client, `"Asgardv1:{id}"`, configured on first use by `MTLSAsgardClientOptions`. It has to
  be per resource because connections are pooled and reused without re-checking the certificate.
- `ClientMTLSAndExchangeHandler`, on every request:
  - refuses `http://` URLs (they would silently skip mTLS) and throws `ResourceIdentityNotRegistered` before
    enrollment is approved;
  - attaches `ExchangeContextTokenForApplicationToken(clientId)` as a `Bearer` token - hence the attribute's target;
  - answers a `401 DelegationProofNotFound` challenge: checks the challenge is signed over its token's `jti`, signs a
    `delegation+jwt` with `resource.key` (`delegate_cnf.spt` = SHA-256 of the called resource's key, `ath` = hash of
    the bearer token), adds a Tide witness signature with `tide.key` when `Require-Tide-Delegation: true`, and replays.
- `AsgardMessageHandler` sits in front of it: `Asgard-*` headers out, Asgard errors back as `AsgardException`.
- Pass a fixed set of ids from code, never ids from a request - each one keeps its connections for the app's lifetime.
- A called resource calls the next one the same way, so delegation chains through any number of resources.

## Accepting mTLS calls and certificate-bound tokens

`RequireTokenBinding(options => options.SupportBindings = Binding.mTLS)` does both halves (see `SupportMTLSBoundTokens`
in `SetupHelpers/TokenBindingSetup.cs`):

- **TLS (Kestrel's https defaults):** presents this resource's enrolled certificate, requires a client certificate,
  and only completes the handshake when the realm root CA issued it for client authentication
  (`ValidateResourceClientCertificate`). Both read `CertificateRegisterSingleton`, so it starts working the moment
  enrollment is approved; before that, connections just fail.
- **JWT:** a token with `cnf.x5t#S256` is only valid on a connection whose client certificate has that SHA-256
  thumbprint. `AsgardJwtBearerPostConfigureOptions` runs one check per enabled binding; one has to pass, and it sets
  `HttpContext.Items["Asgard.TokenBinding"]` to `"dpop"` or `"mtls"`, which the approval filters read to pick the key
  a delegation must be signed with.

Things to know:

- Only https endpoints get it (`ASPNETCORE_HTTPS_PORTS` or an https url), not ones made with `Listen` beforehand.
- Every https endpoint then requires a realm client certificate, so browsers can't use https on that app.
- Certificate-bound tokens use the `Bearer` scheme. The DPoP check leaves tokens without `cnf.jkt` alone, so
  `Binding.DPoP | Binding.mTLS` works.
- There is no public hook for extra TLS settings. Don't call `ConfigureHttpsDefaults` yourself - the last call
  replaces Asgard's.
