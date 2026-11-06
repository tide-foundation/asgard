import { execFileSync } from 'node:child_process';
import * as crypto from 'node:crypto';
import * as fs from 'node:fs';
import * as http from 'node:http';
import * as https from 'node:https';
import * as path from 'node:path';
import type { APIRequestContext } from '@playwright/test';
import { KC_BASE_URL, downloadAdaptorConfig } from './tidecloak';

/**
 * What a client of a realm does without a browser and without a Tide account: get tokens for a password user, prove
 * possession of a DPoP key, sign a resource delegation, get a resource certificate, and exchange a token over mTLS.
 */

export const sha256 = (data: string | Buffer): string => crypto.createHash('sha256').update(data).digest('base64url');

export const epochSeconds = (): number => Math.floor(Date.now() / 1000);

const encode = (json: object): string => Buffer.from(JSON.stringify(json)).toString('base64url');

export function signJwt(header: object, claims: object, key: crypto.KeyObject): string {
  const signingInput = `${encode(header)}.${encode(claims)}`;
  return `${signingInput}.${crypto.sign(null, Buffer.from(signingInput), key).toString('base64url')}`;
}

/** An Ed25519 key, the kind tidecloak-js holds for DPoP. */
export interface DPoPKey {
  privateKey: crypto.KeyObject;
  jwk: crypto.JsonWebKey;
}

export function newDPoPKey(): DPoPKey {
  const { publicKey, privateKey } = crypto.generateKeyPairSync('ed25519');
  return { privateKey, jwk: publicKey.export({ format: 'jwk' }) };
}

/** Header fields and claims to put in place of, or next to, the valid ones. An undefined claim is left out. */
export interface JwtChange {
  header?: object;
  claims?: object;
}

export function dpopProof(key: DPoPKey, htm: string, htu: string, accessToken?: string, change: JwtChange = {}): string {
  return signJwt(
    { typ: 'dpop+jwt', alg: 'EdDSA', jwk: key.jwk, ...change.header },
    { jti: crypto.randomUUID(), htm, htu, iat: epochSeconds(), ath: accessToken && sha256(accessToken), ...change.claims },
    key.privateKey,
  );
}

/** The user's approval for the resource holding resourcePublicKey (its SubjectPublicKeyInfo) to act with accessToken. */
export function resourceDelegation(
  key: DPoPKey,
  accessToken: string,
  resourcePublicKey: Buffer,
  change: JwtChange = {},
): string {
  return signJwt(
    { typ: 'delegation+jwt', alg: 'EdDSA', jwk: key.jwk, ...change.header },
    {
      jti: crypto.randomUUID(),
      iat: epochSeconds(),
      delegate_cnf: { spt: sha256(resourcePublicKey) },
      ath: sha256(accessToken),
      ...change.claims,
    },
    key.privateKey,
  );
}

export interface Reply {
  status: number;
  headers: http.IncomingHttpHeaders;
  body: string;
}

export interface SendOptions {
  method?: string;
  /** Sent as given, in order - a header listed twice goes out twice. */
  headers?: [string, string][];
  body?: string;
  tls?: https.RequestOptions;
}

/** One request on its own connection, so every https request makes its own TLS handshake. */
export function send(url: string, { method = 'GET', headers = [], body, tls }: SendOptions = {}): Promise<Reply> {
  const target = new URL(url);
  const all: [string, string][] = [...headers];
  if (!all.some(([name]) => name.toLowerCase() === 'host')) all.unshift(['Host', target.host]);
  if (body !== undefined) all.push(['Content-Length', String(Buffer.byteLength(body))]);

  const client = target.protocol === 'https:' ? https : http;
  return new Promise((resolve, reject) => {
    const req = client.request(url, { method, headers: all.flat(), agent: false, ...tls }, (res) => {
      let text = '';
      res.on('data', (chunk) => (text += chunk));
      res.on('end', () => resolve({ status: res.statusCode!, headers: res.headers, body: text }));
    });
    req.on('error', reject);
    req.end(body);
  });
}

function accessTokenFrom(reply: Reply, what: string): string {
  let token: string | undefined;
  try {
    token = JSON.parse(reply.body).access_token;
  } catch {
    // reported below
  }
  if (!token) throw new Error(`${what} failed with HTTP ${reply.status}: ${reply.body.slice(0, 300)}`);
  return token;
}

const form = (fields: Record<string, string>): SendOptions => ({
  method: 'POST',
  headers: [['Content-Type', 'application/x-www-form-urlencoded']],
  body: new URLSearchParams(fields).toString(),
});

export interface PasswordUser {
  username: string;
  password: string;
}

/** A public client's password grant. The token is DPoP-bound to key when there is one, and bound to nothing when not. */
export async function passwordGrant(realm: string, clientId: string, user: PasswordUser, key?: DPoPKey): Promise<string> {
  const url = `${KC_BASE_URL}/realms/${realm}/protocol/openid-connect/token`;
  const request = form({ grant_type: 'password', client_id: clientId, ...user });
  if (key) request.headers!.push(['DPoP', dpopProof(key, 'POST', url)]);
  return accessTokenFrom(await send(url, request), `password grant for ${clientId}`);
}

/** A resource's mTLS identity: its private key, and the certificate the realm CA signed for it. */
export interface ResourceIdentity {
  clientId: string;
  key: string;
  certificate: string;
  rootCa: string;
}

/**
 * Asks the realm to certify a new P-256 key for a tide-mtls client, the way Asgard's enrollment does, and returns the
 * key's file. The certificate is ready once the change requests this files are approved.
 */
export async function requestResourceCertificate(
  request: APIRequestContext,
  realm: string,
  clientId: string,
  dir: string,
): Promise<string> {
  const adaptor = await downloadAdaptorConfig(request, realm, clientId);

  const keyFile = path.join(dir, `${clientId}.key`);
  const { privateKey } = crypto.generateKeyPairSync('ec', { namedCurve: 'P-256' });
  fs.writeFileSync(keyFile, privateKey.export({ type: 'pkcs8', format: 'pem' }));

  // the request carries the resource's Tide key as subjectAltPublicKeyInfo, as Asgard's does
  const tideKey = crypto.generateKeyPairSync('ed25519').publicKey.export({ type: 'spki', format: 'der' });
  const csr = execFileSync(
    'openssl',
    ['req', '-new', '-key', keyFile, '-subj', `/CN=client_${clientId}`, '-addext', `2.5.29.72=DER:${tideKey.toString('hex')}`],
    { encoding: 'utf8' },
  );

  const reply = await send(`${KC_BASE_URL}/realms/${realm}/tide-server-identity/request`, {
    method: 'POST',
    headers: [
      ['Authorization', `Bearer ${adaptor.enrollment_token}`],
      ['Content-Type', 'text/plain'],
    ],
    body: csr,
  });
  if (reply.status !== 200) throw new Error(`certificate request for ${clientId} failed with HTTP ${reply.status}: ${reply.body.slice(0, 300)}`);
  return keyFile;
}

export async function collectResourceCertificate(realm: string, clientId: string, keyFile: string): Promise<ResourceIdentity> {
  const key = fs.readFileSync(keyFile, 'utf8');
  const fingerprint = `SHA256:${sha256(crypto.createPublicKey(key).export({ type: 'spki', format: 'der' }))}`;
  const reply = await send(
    `${KC_BASE_URL}/realms/${realm}/tide-server-identity/status?fingerprint=${encodeURIComponent(fingerprint)}`,
  );
  const status = reply.status === 200 ? JSON.parse(reply.body) : undefined;
  if (status?.status !== 'ACTIVE') throw new Error(`no certificate for ${clientId} yet: HTTP ${reply.status} ${reply.body.slice(0, 300)}`);
  return { clientId, key, certificate: status.certificate, rootCa: status.rootCa };
}

export const publicKeyOf = (certificate: string | Buffer): Buffer =>
  new crypto.X509Certificate(certificate).publicKey.export({ type: 'spki', format: 'der' });

/**
 * Exchanges a user's DPoP-bound token as resource, over mTLS with the resource's certificate - the way Asgard's
 * token exchange does. The token that comes back is bound to that certificate.
 */
export async function exchangeOverMTLS(
  realm: string,
  resource: ResourceIdentity,
  userKey: DPoPKey,
  subjectToken: string,
  audience: string,
): Promise<string> {
  // TideCloak serves each realm's mTLS endpoint on its own {realm}.client.{host} name, on the TideCloak host
  const base = new URL(KC_BASE_URL);
  const vhost = `${realm}.client.${base.hostname}`;
  const request = form({
    grant_type: 'urn:ietf:params:oauth:grant-type:token-exchange',
    client_id: resource.clientId,
    subject_token: subjectToken,
    subject_token_type: 'urn:ietf:params:oauth:token-type:access_token',
    actor_token: resourceDelegation(userKey, subjectToken, publicKeyOf(resource.certificate)),
    actor_token_type: 'urn:ietf:params:oauth:token-type:delegation+jwt',
    audience,
  });
  request.headers!.push(['Host', `${vhost}:8443`]);
  request.tls = { servername: vhost, key: resource.key, cert: resource.certificate, ca: resource.rootCa };

  const reply = await send(`https://${base.hostname}:8443/realms/${realm}/protocol/openid-connect/token`, request);
  return accessTokenFrom(reply, `token exchange for ${resource.clientId}`);
}
