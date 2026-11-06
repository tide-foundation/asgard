import type { APIRequestContext } from '@playwright/test';
import { KC_BASE_URL, adminToken } from './tidecloak';

/**
 * Change-request approval, MIRRORING the framework's proven admin client at
 * ~/tidecloak-iga-engine-tests/lib/iga.ts.
 *
 * Why mirrored and not imported: that repo is `private: true` with no entry
 * point and a different @playwright/test version, so a cross-repo import loads
 * two copies of the test runner (same reasoning as lib/iga-engine.ts). And why
 * not shelled out: the framework's CLI runs *recipes*, and there is no recipe
 * for "approve the CRs an already-running app happened to file".
 *
 * Keep this in step with lib/iga.ts over there — it is the source of truth for
 * the lane (authorize then commit) and for the multi-pass dependency ordering.
 */

export interface HttpResult {
  status: number;
  body: any;
  text: string;
}

async function adminRequest(
  request: APIRequestContext,
  method: 'GET' | 'POST',
  path: string,
  json?: any,
): Promise<HttpResult> {
  const token = await adminToken(request);
  const headers: Record<string, string> = { Authorization: `Bearer ${token}` };
  const opts: any = { method, headers, failOnStatusCode: false };
  if (json !== undefined) {
    headers['Content-Type'] = 'application/json';
    opts.data = json;
  }
  const res = await request.fetch(`${KC_BASE_URL}${path}`, opts);
  const text = await res.text();
  let body: any;
  try {
    body = text ? JSON.parse(text) : undefined;
  } catch {
    body = undefined;
  }
  return { status: res.status(), body, text };
}

/** Change requests for a realm, filtered by status (PENDING by default). */
export async function listChangeRequests(
  request: APIRequestContext,
  realm: string,
  status = 'PENDING',
): Promise<any[]> {
  const r = await adminRequest(
    request,
    'GET',
    `/admin/realms/${encodeURIComponent(realm)}/iga/change-requests?status=${encodeURIComponent(status)}`,
  );
  return Array.isArray(r.body) ? r.body : [];
}

export async function getChangeRequest(
  request: APIRequestContext,
  realm: string,
  crId: string,
): Promise<any | undefined> {
  const r = await adminRequest(
    request,
    'GET',
    `/admin/realms/${encodeURIComponent(realm)}/iga/change-requests/${encodeURIComponent(crId)}`,
  );
  return r.status === 200 ? r.body : undefined;
}

/**
 * The BASIC approval lane: self-authorize, then commit. With the default
 * attestor at threshold 1 the master admin can do both with a plain bearer
 * token — no enclave, no browser.
 */
export async function authorizeAndCommit(
  request: APIRequestContext,
  realm: string,
  crId: string,
): Promise<{ authorize: HttpResult; commit: HttpResult }> {
  const base = `/admin/realms/${encodeURIComponent(realm)}/iga/change-requests/${encodeURIComponent(crId)}`;
  const authorize = await adminRequest(request, 'POST', `${base}/authorize`, {});
  const commit = await adminRequest(request, 'POST', `${base}/commit`, {});
  return { authorize, commit };
}

export interface CommitReport {
  /** crId -> the last authorize/commit statuses seen for it. */
  attempts: Record<string, { authorize: number; commit: number; detail: string }>;
  /** Ids that never committed. */
  failures: string[];
  passes: number;
}

/**
 * Authorize + commit an explicit SET of change requests, multi-pass.
 *
 * The passes are what makes dependency-ordered CRs land: a CR whose dependsOn
 * is not yet APPROVED is refused, so it needs a later pass once its dependency
 * commits. Only the ids given are ever touched — a concurrent run's CRs and
 * stale adopt-scan CRs are never swept up.
 */
export async function commitAll(
  request: APIRequestContext,
  realm: string,
  ids: string[],
  maxPasses = 5,
): Promise<CommitReport> {
  const attempts: CommitReport['attempts'] = {};
  let remaining = [...ids];
  let pass = 0;

  for (; pass < maxPasses && remaining.length; pass++) {
    const stillPending: string[] = [];
    let progressed = 0;

    for (const id of remaining) {
      const ac = await authorizeAndCommit(request, realm, id);
      attempts[id] = {
        authorize: ac.authorize.status,
        commit: ac.commit.status,
        detail: (ac.commit.text || ac.authorize.text || '').slice(0, 300),
      };
      if (ac.commit.status >= 200 && ac.commit.status < 300) progressed++;
      else stillPending.push(id);
    }

    remaining = stillPending;
    if (progressed === 0) break; // no forward progress — surface it
  }

  return { attempts, failures: remaining, passes: pass };
}

/**
 * The public server-identity status for an enrolled tide-mtls client. Reports
 * ACTIVE (with the certificate and root CA) only once BOTH the leaf cert and
 * the realm root CA exist — so it is the observable that proves both approvals
 * actually issued something, rather than merely committing.
 */
export async function serverIdentityRealmCertificate(
  request: APIRequestContext,
  realm: string,
): Promise<{ status: number; text: string }> {
  const res = await request.get(
    `${KC_BASE_URL}/realms/${encodeURIComponent(realm)}/tide-server-identity/realmCertificate`,
    { failOnStatusCode: false },
  );
  return { status: res.status(), text: await res.text() };
}
