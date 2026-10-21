import { execFileSync } from 'node:child_process';
import * as crypto from 'node:crypto';
import * as fs from 'node:fs';
import * as path from 'node:path';
import type { PeerCertificate } from 'node:tls';
import { test, expect } from '@playwright/test';
import { type NegativeSuite, SUITE_DIR, loadSuite, serverLog } from '../lib/negativeserver';
import {
  type DPoPKey,
  type JwtChange,
  type Reply,
  type ResourceIdentity,
  dpopProof,
  epochSeconds,
  exchangeOverMTLS,
  newDPoPKey,
  passwordGrant,
  publicKeyOf,
  resourceDelegation,
  send,
  sha256,
} from '../lib/realm-client';

/**
 * Asgard must turn away a request whose DPoP proof, client certificate or resource delegation is wrong. Each group
 * starts with a request that gets through; every other request in the group differs from it in one way, so it is
 * turned away for that reason and no other.
 *
 * Everything is real: negative.setup.ts provisions the realms on the local TideCloak, enrolls the certificates through
 * the realm's change requests and starts Asgard (../negativeserver). The tokens are bob's - a plain password user, not a
 * Tide account.
 */

/** The recipes' password user. */
const BOB = { username: 'bob', password: 'Passw0rd!' };

let suite: NegativeSuite;
let resourceA: ResourceIdentity;
let resourceB: ResourceIdentity;

/** Bob's DPoP key, which his tokens are bound to. */
const bobKey: DPoPKey = newDPoPKey();
/** DPoP-bound to bobKey, for Asgard. */
let boundToken: string;
/** Bob's token from the same client, requested without a DPoP proof. */
let unboundToken: string;
/** Exchanged by resource-a over mTLS, so bound to resource-a's certificate. */
let tokenBoundToA: string;
let asgardPublicKey: Buffer;

test.beforeAll(async () => {
  suite = loadSuite();
  resourceA = suite.resourceA!;
  resourceB = suite.resourceB!;
  asgardPublicKey = publicKeyOf(fs.readFileSync(suite.server!.certificatePath));

  boundToken = await passwordGrant(suite.realm!, 'caller', BOB, bobKey);
  unboundToken = await passwordGrant(suite.realm!, 'caller', BOB);
  tokenBoundToA = await exchangeOverMTLS(suite.realm!, resourceA, bobKey, boundToken, 'asgard');
});

test.afterEach(async ({}, testInfo) => {
  if (testInfo.status !== testInfo.expectedStatus) {
    await testInfo.attach('asgard.log', { body: serverLog(suite.server!), contentType: 'text/plain' });
  }
});

/** A rejection with the given status, and the given error in its WWW-Authenticate challenge. */
function expectRejected(reply: Reply, status: number, error: string, description?: RegExp): void {
  const challenge = String(reply.headers['www-authenticate'] ?? '');
  expect(reply.status, `HTTP ${reply.status}, WWW-Authenticate: ${challenge}`).toBe(status);
  expect(challenge).toContain(`error="${error}"`);
  if (description) expect(challenge).toMatch(description);
}

const claimsOf = (token: string) => JSON.parse(Buffer.from(token.split('.')[1], 'base64url').toString());

test.describe('DPoP', () => {
  const url = () => `${suite.server!.httpUrl}/bound`;
  const proofFor = (token: string, change?: JwtChange) => dpopProof(bobKey, 'GET', url(), token, change);
  const dpopRequest = (token: string, proofs: string[], scheme = 'DPoP') =>
    send(url(), { headers: [['Authorization', `${scheme} ${token}`], ...proofs.map((proof): [string, string] => ['DPoP', proof])] });

  test('accepts a DPoP-bound token with a proof signed by the key it is bound to', async () => {
    const reply = await dpopRequest(boundToken, [proofFor(boundToken)]);
    expect(reply.status).toBe(200);
    expect(JSON.parse(reply.body).binding).toBe('dpop');
  });

  test('rejects a DPoP-bound token without a DPoP proof', async () => {
    expectRejected(await dpopRequest(boundToken, []), 400, 'invalid_request');
  });

  test('rejects a DPoP-bound token sent with the Bearer scheme', async () => {
    expectRejected(
      await dpopRequest(boundToken, [proofFor(boundToken)], 'Bearer'),
      401,
      'invalid_token',
      /requires the DPoP authentication scheme/,
    );
  });

  test('rejects a DPoP proof signed by a key the token is not bound to', async () => {
    const otherKey = newDPoPKey();
    expectRejected(await dpopRequest(boundToken, [dpopProof(otherKey, 'GET', url(), boundToken)]), 401, 'invalid_token');
  });

  test('rejects a DPoP proof for another HTTP method', async () => {
    expectRejected(await dpopRequest(boundToken, [proofFor(boundToken, { claims: { htm: 'POST' } })]), 400, 'invalid_dpop_proof');
  });

  test('rejects a DPoP proof for another URL', async () => {
    const otherUrl = `${suite.server!.httpUrl}/delegated`;
    expectRejected(await dpopRequest(boundToken, [proofFor(boundToken, { claims: { htu: otherUrl } })]), 400, 'invalid_dpop_proof');
  });

  test('rejects a DPoP proof issued more than five minutes ago', async () => {
    const stale = proofFor(boundToken, { claims: { iat: epochSeconds() - 10 * 60 } });
    expectRejected(await dpopRequest(boundToken, [stale]), 400, 'invalid_dpop_proof');
  });

  test('rejects a DPoP proof issued in the future', async () => {
    const early = proofFor(boundToken, { claims: { iat: epochSeconds() + 5 * 60 } });
    expectRejected(await dpopRequest(boundToken, [early]), 400, 'invalid_dpop_proof');
  });

  test('rejects a DPoP proof without an access token hash', async () => {
    expectRejected(await dpopRequest(boundToken, [proofFor(boundToken, { claims: { ath: undefined } })]), 400, 'invalid_dpop_proof');
  });

  test('rejects a DPoP proof whose access token hash is for another token', async () => {
    const proof = proofFor(boundToken, { claims: { ath: sha256(unboundToken) } });
    expectRejected(await dpopRequest(boundToken, [proof]), 400, 'invalid_dpop_proof');
  });

  test('rejects an unsigned DPoP proof', async () => {
    const [header, claims] = proofFor(boundToken, { header: { alg: 'none' } }).split('.');
    expectRejected(await dpopRequest(boundToken, [`${header}.${claims}.`]), 400, 'invalid_dpop_proof');
  });

  test('rejects a DPoP proof signed with HS256', async () => {
    const [header, claims] = proofFor(boundToken, { header: { alg: 'HS256' } }).split('.');
    // the key confusion attack: the proof's own public key as the HMAC secret
    const signature = crypto
      .createHmac('sha256', Buffer.from(bobKey.jwk.x!, 'base64url'))
      .update(`${header}.${claims}`)
      .digest('base64url');
    expectRejected(await dpopRequest(boundToken, [`${header}.${claims}.${signature}`]), 400, 'invalid_dpop_proof');
  });

  test('rejects a DPoP proof whose jwk carries the private key', async () => {
    const privateJwk = bobKey.privateKey.export({ format: 'jwk' });
    expectRejected(await dpopRequest(boundToken, [proofFor(boundToken, { header: { jwk: privateJwk } })]), 400, 'invalid_dpop_proof');
  });

  test('rejects a request with two DPoP proofs', async () => {
    expectRejected(await dpopRequest(boundToken, [proofFor(boundToken), proofFor(boundToken)]), 400, 'invalid_request');
  });

  test('rejects an access token that is not bound to any key', async () => {
    expectRejected(await dpopRequest(unboundToken, [proofFor(unboundToken)]), 401, 'invalid_token');
  });

  test('rejects an expired access token', async () => {
    const expiring = await passwordGrant(suite.realm!, 'caller-short-lived', BOB, bobKey);
    await new Promise((resolve) => setTimeout(resolve, (claimsOf(expiring).exp + 1) * 1000 - Date.now()));
    expectRejected(await dpopRequest(expiring, [proofFor(expiring)]), 401, 'invalid_token', /The token expired at/);
  });

  test('rejects an access token issued for another audience', async () => {
    const forOtherApi = await passwordGrant(suite.realm!, 'caller-for-other-api', BOB, bobKey);
    expectRejected(await dpopRequest(forOtherApi, [proofFor(forOtherApi)]), 401, 'invalid_token', /The audience '.*' is invalid/);
  });

  test('rejects an access token issued by another realm', async () => {
    const fromOtherRealm = await passwordGrant(suite.otherRealm!, 'caller', BOB, bobKey);
    expectRejected(await dpopRequest(fromOtherRealm, [proofFor(fromOtherRealm)]), 401, 'invalid_token', /The issuer '.*' is invalid/);
  });

  test('rejects an access token whose claims were changed after signing', async () => {
    const [header, claims, signature] = boundToken.split('.');
    const changed = Buffer.from(JSON.stringify({ ...claimsOf(boundToken), preferred_username: 'mallory' })).toString('base64url');
    const tampered = `${header}.${changed}.${signature}`;
    expectRejected(await dpopRequest(tampered, [proofFor(tampered)]), 401, 'invalid_token', /signature/);
  });
});

test.describe('mTLS', () => {
  const url = () => `${suite.server!.httpsUrl}/bound`;

  // Asgard presents its resource certificate, which names the resource rather than a host
  const isAsgard = (_host: string, certificate: PeerCertificate) =>
    certificate.subject.CN === 'client_asgard' ? undefined : new Error(`connected to ${certificate.subject.CN}, not client_asgard`);
  const mtlsRequest = (token: string, clientCertificate?: { key: string; cert: string }, onAsgardPresented = () => {}) =>
    send(url(), {
      headers: [['Authorization', `Bearer ${token}`]],
      tls: {
        ca: resourceA.rootCa,
        checkServerIdentity: (host: string, certificate: PeerCertificate) => {
          const notAsgard = isAsgard(host, certificate);
          if (!notAsgard) onAsgardPresented();
          return notAsgard;
        },
        ...clientCertificate,
      },
    });
  const certificateOf = (resource: ResourceIdentity) => ({ key: resource.key, cert: resource.certificate });

  /**
   * TLS 1.3 finishes the client's side of the handshake - checking Asgard's certificate included - before Asgard has
   * checked the client certificate. So a refused certificate shows as the connection dropping after Asgard presented
   * its own; one that drops before that never got as far as the client certificate.
   */
  async function expectClientCertificateRefused(clientCertificate?: { key: string; cert: string }): Promise<void> {
    let asgardPresented = false;
    const error = await mtlsRequest(tokenBoundToA, clientCertificate, () => (asgardPresented = true)).then(
      (answer) => {
        throw new Error(`the connection should have been refused, but Asgard answered HTTP ${answer.status}`);
      },
      (refused: NodeJS.ErrnoException) => refused,
    );
    expect(asgardPresented, `the connection dropped (${error.code}) before Asgard presented its certificate`).toBe(true);
    expect(error.code).toMatch(/^(ECONNRESET|EPIPE|ERR_SSL_)/);
  }

  function selfSignedCertificate(subject: string): { key: string; cert: string } {
    const key = path.join(SUITE_DIR, 'self-signed.key');
    const cert = path.join(SUITE_DIR, 'self-signed.crt');
    execFileSync('openssl', [
      'req', '-x509', '-newkey', 'ec', '-pkeyopt', 'ec_paramgen_curve:P-256', '-nodes', '-days', '1',
      '-subj', `/CN=${subject}`, '-addext', 'extendedKeyUsage=serverAuth,clientAuth', '-keyout', key, '-out', cert,
    ], { stdio: 'ignore' });
    return { key: fs.readFileSync(key, 'utf8'), cert: fs.readFileSync(cert, 'utf8') };
  }

  test('accepts a certificate-bound token on a connection with that certificate', async () => {
    const reply = await mtlsRequest(tokenBoundToA, certificateOf(resourceA));
    expect(reply.status).toBe(200);
    expect(JSON.parse(reply.body).binding).toBe('mtls');
  });

  test('refuses a connection without a client certificate', async () => {
    await expectClientCertificateRefused();
  });

  test('refuses a client certificate the realm CA did not issue', async () => {
    await expectClientCertificateRefused(selfSignedCertificate('client_resource-a'));
  });

  test('rejects a token bound to one certificate on a connection with another', async () => {
    expectRejected(await mtlsRequest(tokenBoundToA, certificateOf(resourceB)), 401, 'invalid_token');
  });
});

test.describe('resource delegation', () => {
  const url = () => `${suite.server!.httpUrl}/delegated`;
  const delegatedRequest = (delegation?: string) =>
    send(url(), {
      headers: [
        ['Authorization', `DPoP ${boundToken}`],
        ['DPoP', dpopProof(bobKey, 'GET', url(), boundToken)],
        ...(delegation ? [['Resource-Delegation', delegation] as [string, string]] : []),
      ],
    });
  const delegationFor = (change?: JwtChange) => resourceDelegation(bobKey, boundToken, asgardPublicKey, change);

  test('accepts a delegation to this resource signed with the key the token is bound to', async () => {
    const reply = await delegatedRequest(delegationFor());
    expect(reply.status).toBe(200);
    expect(JSON.parse(reply.body).delegated).toBe(true);
  });

  test('challenges a request without a delegation to sign one', async () => {
    const reply = await delegatedRequest();
    expectRejected(reply, 401, 'DelegationProofNotFound');
    expect(reply.headers['resource-delegation-key']).toBeTruthy();
    expect(reply.headers['resource-delegation-challenge']).toBeTruthy();
  });

  test('rejects a delegation that is not a delegation+jwt', async () => {
    expectRejected(await delegatedRequest(delegationFor({ header: { typ: 'dpop+jwt' } })), 401, 'DelegationInvalid');
  });

  test('rejects a delegation issued longer ago than it is valid for', async () => {
    expectRejected(await delegatedRequest(delegationFor({ claims: { iat: epochSeconds() - 15 * 60 } })), 401, 'DelegationInvalid');
  });

  test('rejects a delegation issued in the future', async () => {
    expectRejected(await delegatedRequest(delegationFor({ claims: { iat: epochSeconds() + 5 * 60 } })), 401, 'DelegationInvalid');
  });

  test('rejects a delegation signed by a key the token is not bound to', async () => {
    const otherKey = newDPoPKey();
    expectRejected(await delegatedRequest(resourceDelegation(otherKey, boundToken, asgardPublicKey)), 401, 'DelegationInvalid');
  });

  test('rejects a delegation whose jwk carries the private key', async () => {
    const privateJwk = bobKey.privateKey.export({ format: 'jwk' });
    expectRejected(await delegatedRequest(delegationFor({ header: { jwk: privateJwk } })), 401, 'DelegationInvalid');
  });

  test('rejects a delegation to another resource', async () => {
    const toResourceA = resourceDelegation(bobKey, boundToken, publicKeyOf(resourceA.certificate));
    expectRejected(await delegatedRequest(toResourceA), 401, 'DelegationInvalid');
  });

  test('rejects a delegation whose access token hash is for another token', async () => {
    expectRejected(await delegatedRequest(delegationFor({ claims: { ath: sha256(unboundToken) } })), 401, 'DelegationInvalid');
  });
});
