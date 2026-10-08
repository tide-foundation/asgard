import type { APIRequestContext } from '@playwright/test';

/**
 * Minimal, READ-ONLY TideCloak admin-REST helpers.
 *
 * Deliberately tiny. Anything that MUTATES a realm belongs in the
 * iga-engine-tests framework (see lib/iga-engine.ts) — realm writes on an
 * IGA realm go through governance, and duplicating that logic here would mean
 * two implementations of the same governance semantics drifting apart.
 *
 * Env names mirror the framework's so one set of vars configures both suites.
 */

export const KC_BASE_URL = process.env.KC_BASE_URL ?? 'http://localhost:8080';
const ADMIN_USER = process.env.KC_ADMIN_USER ?? 'admin';
const ADMIN_PASSWORD = process.env.KC_ADMIN_PASSWORD ?? 'password';

/** Master-realm admin access token via the admin-cli password grant. */
export async function adminToken(request: APIRequestContext): Promise<string> {
  const res = await request.post(
    `${KC_BASE_URL}/realms/master/protocol/openid-connect/token`,
    {
      form: {
        grant_type: 'password',
        client_id: 'admin-cli',
        username: ADMIN_USER,
        password: ADMIN_PASSWORD,
      },
      failOnStatusCode: false,
    },
  );
  if (!res.ok()) {
    throw new Error(
      `could not obtain a TideCloak admin token from ${KC_BASE_URL} ` +
        `(HTTP ${res.status()}). Is the local Tide stack up?`,
    );
  }
  return (await res.json()).access_token as string;
}

async function adminGet(request: APIRequestContext, path: string): Promise<any> {
  const token = await adminToken(request);
  const res = await request.get(`${KC_BASE_URL}${path}`, {
    headers: { Authorization: `Bearer ${token}`, Accept: 'application/json' },
    failOnStatusCode: false,
  });
  if (res.status() === 404) return undefined;
  if (!res.ok()) throw new Error(`GET ${path} -> HTTP ${res.status()}: ${(await res.text()).slice(0, 200)}`);
  return res.json();
}

/** True when TideCloak is reachable at all (no credentials needed). */
export async function isTidecloakReachable(request: APIRequestContext): Promise<boolean> {
  try {
    const res = await request.get(`${KC_BASE_URL}/realms/master/.well-known/openid-configuration`, {
      failOnStatusCode: false,
      timeout: 5_000,
    });
    return res.ok();
  } catch {
    return false;
  }
}

export async function listRealmNames(request: APIRequestContext): Promise<string[]> {
  const body = await adminGet(request, '/admin/realms');
  return (Array.isArray(body) ? body : []).map((r: any) => r.realm).filter(Boolean);
}

export interface RealmShape {
  exists: boolean;
  igaEnabled: boolean;
  defaultSignatureAlgorithm?: string;
  /** tide-vendor-key present AND holding no local private key => ORK signing path. */
  orkPath: boolean;
}

/**
 * Read back the properties that make a realm a *Tide* realm. Mirrors the
 * framework's own post-bootstrap assertions (lib/bootstrap.ts checkOrkPath), so
 * "the realm survived" also means "it survived intact".
 */
export async function inspectRealm(request: APIRequestContext, realm: string): Promise<RealmShape> {
  const rep = await adminGet(request, `/admin/realms/${realm}`);
  if (!rep) return { exists: false, igaEnabled: false, orkPath: false };

  const comps =
    (await adminGet(request, `/admin/realms/${realm}/components?type=org.keycloak.keys.KeyProvider`)) ?? [];
  const vendorKey = (Array.isArray(comps) ? comps : []).find(
    (c: any) => c.providerId === 'tide-vendor-key',
  );
  const localKey = vendorKey?.config?.eddsaPrivateKey?.[0];

  return {
    exists: true,
    igaEnabled: rep?.attributes?.isIGAEnabled === 'true',
    defaultSignatureAlgorithm: rep?.defaultSignatureAlgorithm,
    orkPath: !!vendorKey && !localKey,
  };
}

/**
 * A client's representation by clientId (the human name), not by its UUID.
 * Undefined when the realm has no such client.
 */
export async function getClient(
  request: APIRequestContext,
  realm: string,
  clientId: string,
): Promise<any | undefined> {
  const found = await adminGet(
    request,
    `/admin/realms/${encodeURIComponent(realm)}/clients?clientId=${encodeURIComponent(clientId)}`,
  );
  return (Array.isArray(found) ? found : []).find((c: any) => c.clientId === clientId);
}

/** Protocol mappers on a client, looked up by clientId. Empty when the client is absent. */
export async function getProtocolMappers(
  request: APIRequestContext,
  realm: string,
  clientId: string,
): Promise<any[]> {
  const client = await getClient(request, realm, clientId);
  if (!client?.id) return [];
  const mappers = await adminGet(
    request,
    `/admin/realms/${encodeURIComponent(realm)}/clients/${client.id}/protocol-mappers/models`,
  );
  return Array.isArray(mappers) ? mappers : [];
}

/**
 * The only installation provider TideCloak augments. Any other providerId is
 * passed through to stock Keycloak unmodified, so the Tide fields the Asgard
 * adaptor needs (jwk, vendorId, gVVK, enrollment_token, …) would be missing.
 * See VendorResource.getInstallationProvider in tidecloak-idp-extensions.
 */
export const TIDE_ADAPTOR_PROVIDER = 'keycloak-oidc-keycloak-json';

/**
 * Download a client's adaptor config — the `keycloak.json` a Tide app ships.
 *
 * This is NOT stock Keycloak's `/clients/{id}/installation/providers/{p}`: Tide
 * wraps that endpoint to graft on the vendor key, ORK/vault settings, signed
 * client origins, and — for a `tide-mtls` client, and only when the caller can
 * manage the realm — a freshly minted `enrollment_token`.
 *
 * @param clientId the human clientId ("frontend"); resolved to the UUID the
 *   endpoint actually wants. Its query param is confusingly also named
 *   `clientId` but is matched with realm.getClientById(), i.e. the UUID.
 */
export async function downloadAdaptorConfig(
  request: APIRequestContext,
  realm: string,
  clientId: string,
  providerId: string = TIDE_ADAPTOR_PROVIDER,
): Promise<any> {
  const client = await getClient(request, realm, clientId);
  if (!client?.id) throw new Error(`realm ${realm} has no client "${clientId}"`);

  const config = await adminGet(
    request,
    `/admin/realms/${encodeURIComponent(realm)}/vendorResources/get-installations-provider` +
      `?clientId=${encodeURIComponent(client.id)}&providerId=${encodeURIComponent(providerId)}`,
  );
  if (!config) {
    throw new Error(
      `no adaptor config for "${clientId}" in realm ${realm} (provider ${providerId}) — ` +
        `the vendorResources endpoint 404'd, so this realm may not be Tide-enabled`,
    );
  }
  return config;
}

/** A user's representation by exact username. Undefined when absent. */
export async function getUser(
  request: APIRequestContext,
  realm: string,
  username: string,
): Promise<any | undefined> {
  const found = await adminGet(
    request,
    `/admin/realms/${encodeURIComponent(realm)}/users?exact=true&username=${encodeURIComponent(username)}`,
  );
  return (Array.isArray(found) ? found : [])[0];
}
