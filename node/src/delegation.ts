import { createHash, randomUUID, generateKeyPairSync, createPrivateKey, createPublicKey } from 'node:crypto'
import { Agent } from 'node:https'
import { readFileSync, writeFileSync, existsSync, mkdirSync } from 'node:fs'
import { join, dirname } from 'node:path'

export interface ServerIdentity {
  /** SPIFFE ID */
  spiffeId: string
  /** PEM-encoded X.509 certificate (VVK-signed SVID) */
  certificate: string
  /** PEM-encoded trust bundle (VVK CA cert) */
  trustBundle: string
  /** Instance ID */
  instanceId?: string
}

export interface DelegationConfig {
  /** TideCloak server URL */
  tidecloakUrl: string
  /** TideCloak realm name */
  realm: string
  /** Client ID registered in TideCloak (public, browser auth) */
  clientId: string
  /** Server client ID for token exchange (confidential, mTLS auth) */
  serverClientId?: string
  /** Custom fetch implementation */
  fetch?: typeof globalThis.fetch
  /** Server identity for mTLS (loaded from tidecloak.json serverIdentity) */
  serverIdentity?: ServerIdentity
  /** PEM-encoded private key for mTLS (if providing directly) */
  privateKey?: string
  /** Path to tidecloak.json (auto-loads serverIdentity if present) */
  adapterJsonPath?: string
  /**
   * Overall timeout (ms) for the automatic cert status poll started after a
   * cert request. If the request is not approved within this window the poll
   * stops and the server runs without mTLS until the next restart re-polls.
   * Default: CERT_POLL_TIMEOUT_MS.
   */
  certPollTimeoutMs?: number
  /**
   * One-time enrollment token used to authenticate the server-cert request
   * endpoint (sent as `Authorization: Bearer <token>`). An explicit
   * accessToken argument to requestServerCert() takes precedence over this.
   */
  enrollmentToken?: string
}

/** Persisted cert file names written alongside server.key in keyDir. */
const CERT_FILE_NAME = 'server.crt'
const TRUST_BUNDLE_FILE_NAME = 'server-trust-bundle.pem'
const SPIFFE_FILE_NAME = 'server-spiffe-id'

/** Initial poll interval (ms) for cert status. */
const CERT_POLL_INITIAL_MS = 4000
/** Maximum poll interval (ms) after backoff. */
const CERT_POLL_MAX_MS = 30000
/** Default overall poll timeout (ms) - a few minutes. */
const CERT_POLL_TIMEOUT_MS = 5 * 60 * 1000

export interface DelegationRequest {
  /** Target audience */
  audience?: string
  /** Requested scope */
  scope?: string
  /** Additional claims */
  [key: string]: unknown
}

export interface PackedDelegationRequest {
  /** The packed request to be signed by the browser */
  payload: Record<string, unknown>
}

export interface DelegationResult {
  /** The signed delegation token */
  access_token: string
  /** Token type */
  token_type: string
  /** Seconds until expiry */
  expires_in: number
  /** The issued token type URI */
  issued_token_type: string
}

/** Cached delegation token for a user session */
interface DelegationCache {
  token: string
  expiresAt: number
}

export class TideDelegation {
  private config: DelegationConfig

  /** Per-session delegation token cache */
  private cache = new Map<string, DelegationCache>()

  /** HTTPS agent for mTLS */
  private mtlsAgent: Agent | null = null

  /** Server identity (SPIFFE SVID) */
  private serverIdentity: ServerIdentity | null = null

  /** SHA-256 thumbprint of the server cert (for cnf.x5t#S256 binding) */
  private certThumbprint: string | null = null

  /** In-flight background cert status poll, if any. */
  private certPoll: Promise<void> | null = null

  constructor(config: DelegationConfig) {
    this.config = config

    // Load server identity from adapter JSON if path provided
    if (config.adapterJsonPath && !config.serverIdentity) {
      try {
        const adapterJson = JSON.parse(readFileSync(config.adapterJsonPath, 'utf-8'))
        if (adapterJson.serverIdentity) {
          config.serverIdentity = adapterJson.serverIdentity
        }
        // Auto-load the server client ID from the adapter JSON when not set, so
        // enrollment does not silently no-op when serverClientId is omitted.
        if (!config.serverClientId) {
          const serverResource = adapterJson.serverResource ?? adapterJson.resource
          if (serverResource) config.serverClientId = serverResource
        }
      } catch {
        // Adapter JSON not found or no serverIdentity
      }
    }

    // Configure mTLS if server identity + key are available
    if (config.serverIdentity) {
      this.serverIdentity = config.serverIdentity
      this.certThumbprint = this.computeCertThumbprint(config.serverIdentity.certificate)

      if (config.privateKey) {
        this.mtlsAgent = new Agent({
          cert: config.serverIdentity.certificate,
          key: config.privateKey,
          ca: config.serverIdentity.trustBundle,
          rejectUnauthorized: true,
        })
      }
    }
  }

  /**
   * Initialize the delegation system.
   *
   * On first run: generates an Ed25519 keypair, saves private key to data/server.key,
   * and submits a certificate request to TideCloak.
   * On restart: loads existing key from file and configures mTLS.
   *
   * @param keyDir - Directory to store key and instance ID (default: same dir as adapter JSON, or ./data)
   * @param accessToken - Optional bearer token forwarded to the TideCloak cert-request endpoint
   */
  async init(keyDir?: string, accessToken?: string): Promise<void> {
    const dir = keyDir ?? (this.config.adapterJsonPath ? dirname(this.config.adapterJsonPath) : join(process.cwd(), 'data'))
    if (!existsSync(dir)) mkdirSync(dir, { recursive: true })

    // Already have mTLS configured (key was passed directly)
    if (this.isMtlsEnabled()) {
      console.log('[tide-server] mTLS already configured')
      return
    }

    // Prefer a previously persisted cert from keyDir over a (re-)request.
    // This is written by the automatic status poll on a prior run, so a
    // restart does not re-request an already-issued certificate.
    if (!this.serverIdentity?.certificate) {
      this.loadPersistedCert(dir)
    }

    const keyPath = join(dir, 'server.key')

    if (existsSync(keyPath)) {
      // Load existing key
      console.log('[tide-server] Loading existing key from', keyPath)
      const privateKeyPem = readFileSync(keyPath, 'utf-8')
      if (this.serverIdentity) {
        this.setMtlsKey(privateKeyPem)
      }
      // Request cert if not already present
      if (!this.serverIdentity?.certificate) {
        await this.requestServerCert(dir, accessToken)
      }
      return
    }

    // Generate new key and save
    console.log('[tide-server] Generating new Ed25519 keypair...')
    const { publicKey, privateKey } = generateKeyPairSync('ed25519')
    const privateKeyPem = privateKey.export({ format: 'pem', type: 'pkcs8' }) as string
    writeFileSync(keyPath, privateKeyPem)
    console.log('[tide-server] Private key saved to', keyPath)

    // Configure mTLS if cert already available
    if (this.serverIdentity) {
      this.setMtlsKey(privateKeyPem)
    }

    // Request cert
    await this.requestServerCert(dir, accessToken)
  }

  /**
   * Request a server identity certificate from TideCloak.
   * Submits the public key derived from the saved private key.
   * The cert must be approved by admin quorum before mTLS works.
   *
   * @param keyDir - Directory containing server.key and server-instance-id
   * @param accessToken - Optional bearer token sent as Authorization header
   */
  async requestServerCert(keyDir?: string, accessToken?: string): Promise<void> {
    // Already have a cert - nothing to do
    if (this.serverIdentity?.certificate) {
      console.log('[tide-server] Server certificate already present')
      return
    }

    const dir = keyDir ?? (this.config.adapterJsonPath ? dirname(this.config.adapterJsonPath) : join(process.cwd(), 'data'))
    if (!existsSync(dir)) mkdirSync(dir, { recursive: true })

    // Load or generate instance ID
    const instanceIdPath = join(dir, 'server-instance-id')
    let instanceId: string
    try {
      instanceId = readFileSync(instanceIdPath, 'utf-8').trim()
    } catch {
      instanceId = randomUUID()
      writeFileSync(instanceIdPath, instanceId)
    }

    // Derive public key from saved private key
    const keyPath = join(dir, 'server.key')
    let pubB64url: string
    if (existsSync(keyPath)) {
      const privKey = createPrivateKey({ key: readFileSync(keyPath), format: 'pem', type: 'pkcs8' })
      const pubKey = createPublicKey(privKey)
      const pubDer = pubKey.export({ format: 'der', type: 'spki' })
      pubB64url = Buffer.from(pubDer).subarray(-32).toString('base64url')
    } else {
      // No key yet - generate one
      const { publicKey, privateKey } = generateKeyPairSync('ed25519')
      writeFileSync(keyPath, privateKey.export({ format: 'pem', type: 'pkcs8' }) as string)
      const pubDer = publicKey.export({ format: 'der', type: 'spki' })
      pubB64url = Buffer.from(pubDer).subarray(-32).toString('base64url')
    }

    // Submit cert request (public endpoint; optional bearer token)
    const fetchFn = this.config.fetch ?? globalThis.fetch
    const requestUrl = `${this.config.tidecloakUrl.replace(/\/+$/, '')}/realms/${this.config.realm}/tide-server-identity/request`

    if (!this.config.serverClientId) {
      console.warn('[tide-server] No serverClientId configured (missing serverResource in adapter JSON). Skipping cert request.')
      return
    }
    const effectiveClientId = this.config.serverClientId
    console.log(`[tide-server] Requesting certificate for client=${effectiveClientId} instance=${instanceId}`)

    try {
      const headers: Record<string, string> = { 'Content-Type': 'application/json' }
      // Explicit arg wins; otherwise fall back to the configured enrollment token.
      const token = accessToken ?? this.config.enrollmentToken
      if (token) headers['Authorization'] = `Bearer ${token}`
      const response = await fetchFn(requestUrl, {
        method: 'POST',
        headers,
        body: JSON.stringify({
          clientId: effectiveClientId,
          publicKey: pubB64url,
          instanceId,
          requestedLifetime: 86400,
        }),
      })

      if (response.ok) {
        const result = await response.json() as any
        const changeSetId = result?.changeSetId
        console.log(`[tide-server] Certificate request submitted (changeSetId: ${changeSetId})`)
        console.log(`[tide-server] Approve in TideCloak Admin > Realm Settings > Server Certs`)
        if (changeSetId) {
          this.startCertStatusPoll(String(changeSetId), dir, accessToken)
        } else {
          console.warn('[tide-server] No changeSetId returned - cannot poll for cert status')
        }
      } else if (response.status === 409) {
        // Already pending. Recover the changeSetId if the server returns it so
        // we can poll the existing request rather than re-submitting.
        let changeSetId: string | undefined
        try {
          const body = await response.json() as any
          changeSetId = body?.changeSetId ?? body?.existingChangeSetId
        } catch {
          // body not JSON / no changeSetId
        }
        if (changeSetId) {
          console.log(`[tide-server] Certificate request already pending (changeSetId: ${changeSetId}) - polling existing request`)
          this.startCertStatusPoll(String(changeSetId), dir, accessToken)
        } else {
          console.log('[tide-server] Certificate request already pending - approve in TideCloak Admin')
        }
      } else if (response.status === 401 || response.status === 403) {
        // One-time enrollment token rejected (already consumed or invalid). This
        // is expected on a restart after the first request consumed the token:
        // the cert request is likely already pending admin approval. Do not
        // crash; the persisted cert will be picked up once issued.
        let changeSetId: string | undefined
        try {
          const body = await response.json() as any
          changeSetId = body?.changeSetId ?? body?.existingChangeSetId
        } catch {
          // body not JSON / no changeSetId
        }
        console.warn('[tide-server] Enrollment token rejected (already consumed or invalid); the request may already be pending admin approval. Will use the persisted cert once issued.')
        if (changeSetId) {
          this.startCertStatusPoll(String(changeSetId), dir, accessToken)
        }
        return
      } else {
        const err = await response.text()
        console.warn(`[tide-server] Certificate request failed (${response.status}): ${err}`)
      }
    } catch (err) {
      console.warn(`[tide-server] Could not reach TideCloak: ${(err as Error).message}`)
    }
  }

  /**
   * Start a bounded background poll of the cert status endpoint.
   *
   * The admin approval is asynchronous (a human approves whenever), so this
   * MUST NOT block server startup. The poll runs in the background with
   * interval backoff up to certPollTimeoutMs. On ACTIVE it persists the cert
   * to keyDir and brings up mTLS automatically (no manual re-export). On
   * timeout it logs and returns, leaving the server running without mTLS until
   * the next restart re-polls.
   *
   * The returned promise is also stored on this.certPoll so callers/tests can
   * optionally await it; init()/startup do not await it.
   */
  private startCertStatusPoll(changeSetId: string, keyDir: string, accessToken?: string): Promise<void> {
    if (this.certPoll) return this.certPoll
    this.certPoll = this.pollCertStatus(changeSetId, keyDir, accessToken).finally(() => {
      this.certPoll = null
    })
    // Do not let an unawaited rejection crash the process.
    this.certPoll.catch(() => {})
    return this.certPoll
  }

  private async pollCertStatus(changeSetId: string, keyDir: string, accessToken?: string): Promise<void> {
    const fetchFn = this.config.fetch ?? globalThis.fetch
    const statusUrl = `${this.config.tidecloakUrl.replace(/\/+$/, '')}/realms/${this.config.realm}/tide-server-identity/status?changeSetId=${encodeURIComponent(changeSetId)}`
    const timeoutMs = this.config.certPollTimeoutMs ?? CERT_POLL_TIMEOUT_MS
    const deadline = Date.now() + timeoutMs

    let interval = CERT_POLL_INITIAL_MS
    console.log(`[tide-server] Polling cert status for changeSetId=${changeSetId} (timeout ${Math.round(timeoutMs / 1000)}s)`)

    while (Date.now() < deadline) {
      await new Promise((resolve) => setTimeout(resolve, interval))
      try {
        const headers: Record<string, string> = {}
        if (accessToken) headers['Authorization'] = `Bearer ${accessToken}`
        const response = await fetchFn(statusUrl, { method: 'GET', headers })
        if (!response.ok) {
          console.warn(`[tide-server] Cert status poll failed (${response.status}); will retry`)
        } else {
          const result = await response.json() as any
          if (result?.status === 'ACTIVE' && result.certificate && result.trustBundle) {
            this.applyIssuedCert(
              {
                spiffeId: result.spiffeId,
                certificate: result.certificate,
                trustBundle: result.trustBundle,
              },
              keyDir,
            )
            console.log(`[tide-server] Certificate issued and mTLS enabled (changeSetId=${changeSetId}, spiffeId=${result.spiffeId})`)
            return
          }
          console.log(`[tide-server] Cert request ${changeSetId} status=${result?.status ?? 'unknown'} - still pending approval`)
        }
      } catch (err) {
        console.warn(`[tide-server] Cert status poll error: ${(err as Error).message}; will retry`)
      }
      interval = Math.min(interval * 2, CERT_POLL_MAX_MS)
    }

    console.log(`[tide-server] cert request ${changeSetId} still pending approval; will run without mTLS until approved`)
  }

  /**
   * Persist an issued cert to keyDir, wire up serverIdentity + thumbprint, and
   * bring up the mTLS agent using the existing private key. No manual re-export
   * of tidecloak.json is required.
   */
  private applyIssuedCert(
    issued: { spiffeId: string; certificate: string; trustBundle: string },
    keyDir: string,
  ): void {
    const certPath = join(keyDir, CERT_FILE_NAME)
    const trustPath = join(keyDir, TRUST_BUNDLE_FILE_NAME)
    const spiffePath = join(keyDir, SPIFFE_FILE_NAME)
    writeFileSync(certPath, issued.certificate)
    writeFileSync(trustPath, issued.trustBundle)
    if (issued.spiffeId) writeFileSync(spiffePath, issued.spiffeId)
    console.log(`[tide-server] Persisted certificate to ${certPath} and trust bundle to ${trustPath}`)

    // Recover the instance ID written during the request, if present.
    let instanceId: string | undefined
    try {
      instanceId = readFileSync(join(keyDir, 'server-instance-id'), 'utf-8').trim()
    } catch {
      // no instance id file
    }

    this.serverIdentity = {
      spiffeId: issued.spiffeId,
      certificate: issued.certificate,
      trustBundle: issued.trustBundle,
      instanceId,
    }
    this.certThumbprint = this.computeCertThumbprint(issued.certificate)

    // Bring up the mTLS agent with the persisted private key.
    const keyPath = join(keyDir, 'server.key')
    if (existsSync(keyPath)) {
      this.setMtlsKey(readFileSync(keyPath, 'utf-8'))
    } else {
      console.warn(`[tide-server] Cert issued but ${keyPath} missing - cannot enable mTLS`)
    }
  }

  /**
   * Load a previously persisted cert (server.crt + server-trust-bundle.pem)
   * from keyDir if present, populating serverIdentity + thumbprint. The mTLS
   * agent is wired later in init() once the private key is loaded. Returns true
   * if a persisted cert was loaded.
   */
  private loadPersistedCert(keyDir: string): boolean {
    const certPath = join(keyDir, CERT_FILE_NAME)
    const trustPath = join(keyDir, TRUST_BUNDLE_FILE_NAME)
    if (!existsSync(certPath) || !existsSync(trustPath)) return false
    try {
      const certificate = readFileSync(certPath, 'utf-8')
      const trustBundle = readFileSync(trustPath, 'utf-8')
      let spiffeId = ''
      try {
        spiffeId = readFileSync(join(keyDir, SPIFFE_FILE_NAME), 'utf-8').trim()
      } catch {
        // spiffe id file optional
      }
      let instanceId: string | undefined
      try {
        instanceId = readFileSync(join(keyDir, 'server-instance-id'), 'utf-8').trim()
      } catch {
        // optional
      }
      this.serverIdentity = { spiffeId, certificate, trustBundle, instanceId }
      this.certThumbprint = this.computeCertThumbprint(certificate)
      console.log(`[tide-server] Loaded persisted certificate from ${certPath}`)
      return true
    } catch (err) {
      console.warn(`[tide-server] Failed to load persisted cert: ${(err as Error).message}`)
      return false
    }
  }

  /**
   * Set the private key for mTLS. Call this after loading the key.
   */
  setMtlsKey(privateKeyPem: string): void {
    if (!this.serverIdentity) {
      throw new Error('No serverIdentity configured - cannot set mTLS key')
    }
    this.mtlsAgent = new Agent({
      cert: this.serverIdentity.certificate,
      key: privateKeyPem,
      ca: this.serverIdentity.trustBundle,
      rejectUnauthorized: true,
    })
  }

  /**
   * Check if mTLS is configured and ready.
   */
  isMtlsEnabled(): boolean {
    return this.mtlsAgent !== null && this.certThumbprint !== null
  }

  /**
   * Get the SPIFFE ID.
   */
  getSpiffeId(): string | null {
    return this.serverIdentity?.spiffeId ?? null
  }

  /**
   * Get the cert thumbprint (cnf.x5t#S256).
   */
  getCertThumbprint(): string | null {
    return this.certThumbprint
  }

  /**
   * Compute SHA-256 thumbprint of a PEM certificate (RFC 8705 cnf.x5t#S256).
   */
  private computeCertThumbprint(certPem: string): string {
    const b64 = certPem
      .replace(/-----BEGIN CERTIFICATE-----/g, '')
      .replace(/-----END CERTIFICATE-----/g, '')
      .replace(/\s/g, '')
    const derBytes = Buffer.from(b64, 'base64')
    return createHash('sha256').update(derBytes).digest('base64url')
  }

  /**
   * Fetch with mTLS agent if configured, otherwise plain fetch.
   */
  private async mtlsFetch(url: string, init: RequestInit): Promise<Response> {
    const fetchFn = this.config.fetch ?? globalThis.fetch
    if (this.mtlsAgent && url.startsWith('https://')) {
      return fetchFn(url, { ...init, ...(({ agent: this.mtlsAgent }) as any) })
    }
    if (this.serverIdentity?.certificate && url.startsWith('http://')) {
      // HTTP (local dev): send cert as header for TideMtlsAuthenticator fallback
      const headers = new Headers(init.headers)
      headers.set('X-SSL-Client-Cert', encodeURIComponent(this.serverIdentity.certificate))
      return fetchFn(url, { ...init, headers })
    }
    return fetchFn(url, init)
  }

  /**
   * Pack a delegation request with the server's cert thumbprint.
   * The browser signs this to authorize this specific server.
   */
  packRequest(request: DelegationRequest): PackedDelegationRequest {
    if (!this.certThumbprint) {
      throw new Error('No server certificate configured - cannot pack delegation request')
    }
    const now = Math.floor(Date.now() / 1000)
    return {
      payload: {
        ...request,
        cnf: { 'x5t#S256': this.certThumbprint },
        iat: now,
        exp: now + 300,
        jti: randomUUID(),
      }
    }
  }

  /**
   * Exchange artifacts for a delegation token via mTLS.
   */
  async exchange(params: {
    subjectToken: string
    delegationRequest: string
  }): Promise<DelegationResult> {
    if (!this.isMtlsEnabled()) {
      throw new Error('mTLS not configured - set serverIdentity and privateKey')
    }

    const tokenEndpoint = `${this.config.tidecloakUrl.replace(/\/+$/, '')}/realms/${this.config.realm}/protocol/openid-connect/token`

    const body = new URLSearchParams({
      grant_type: 'urn:ietf:params:oauth:grant-type:token-exchange',
      client_id: this.config.serverClientId ?? this.config.clientId,
      subject_token: params.subjectToken,
      subject_token_type: 'urn:ietf:params:oauth:token-type:access_token',
      actor_token: params.delegationRequest,
      actor_token_type: 'urn:ietf:params:oauth:token-type:jwt',
    })

    const response = await this.mtlsFetch(tokenEndpoint, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/x-www-form-urlencoded',
      },
      body,
    })

    if (!response.ok) {
      const errorText = await response.text()
      throw new Error(`Delegation exchange failed (${response.status}): ${errorText}`)
    }

    return response.json() as Promise<DelegationResult>
  }

  // ============================================
  // Express middleware
  // ============================================

  /**
   * Get cached delegation for a user session, or null if expired/missing.
   */
  private getCached(sessionId: string): DelegationCache | null {
    const cached = this.cache.get(sessionId)
    if (!cached) return null
    if (Date.now() > cached.expiresAt) {
      this.cache.delete(sessionId)
      return null
    }
    return cached
  }

  /**
   * Express middleware: handles POST /api/delegation
   *
   * Receives signed delegation request from the browser,
   * exchanges for a delegation token via mTLS, and caches it.
   *
   * @example
   * app.post('/api/delegation', authenticate, delegation.handleDelegation())
   */
  handleDelegation() {
    return async (req: any, res: any) => {
      try {
        const { signedDelegationRequest } = req.body
        const subjectToken = req.accessToken

        if (!signedDelegationRequest || !subjectToken) {
          res.status(400).json({ error: 'Missing signedDelegationRequest or access token' })
          return
        }

        const result = await this.exchange({
          subjectToken,
          delegationRequest: signedDelegationRequest,
        })

        console.log('[delegation] Token:', result.access_token)

        const sessionId = this.extractSessionId(subjectToken)
        this.cache.set(sessionId, {
          token: result.access_token,
          expiresAt: Date.now() + (result.expires_in * 1000) - 5000,
        })

        res.json({ delegated: true })
      } catch (err: any) {
        res.status(500).json({ error: 'Delegation failed: ' + err.message })
      }
    }
  }

  /**
   * Express middleware: requires delegation for the route.
   *
   * Reads a signed delegation request from the X-Delegation-Request header.
   * If a cached delegation token exists, uses it. Otherwise exchanges the
   * signed request for a delegation token via mTLS, caches it, and attaches
   * req.delegation with a fetch helper.
   *
   * @example
   * app.get('/api/admin/roles',
   *   authenticate,
   *   delegation.requireDelegation(),
   *   async (req, res) => {
   *     const roles = await req.delegation.fetch(adminUrl + '/clients/' + id + '/roles')
   *     res.json({ roles })
   *   }
   * )
   */
  requireDelegation() {
    return async (req: any, res: any, next: any) => {
      const subjectToken = req.accessToken
      if (!subjectToken) {
        res.status(401).json({ error: 'No access token' })
        return
      }

      const sessionId = this.extractSessionId(subjectToken)
      const cached = this.getCached(sessionId)

      if (cached) {
        req.delegation = this.buildDelegationHelper(cached.token)
        next()
        return
      }

      // No cache — exchange the signed delegation request from header
      const signedRequest = req.headers['x-delegation-request']
      if (!signedRequest) {
        res.status(401).json({ error: 'Delegation required — include X-Delegation-Request header' })
        return
      }

      try {
        const result = await this.exchange({
          subjectToken,
          delegationRequest: signedRequest,
        })

        console.log('[delegation] Token:', result.access_token)

        this.cache.set(sessionId, {
          token: result.access_token,
          expiresAt: Date.now() + (result.expires_in * 1000) - 5000,
        })

        req.delegation = this.buildDelegationHelper(result.access_token)
        next()
      } catch (err: any) {
        console.error(`[delegation] Inline exchange failed: ${err.message}`)
        res.status(500).json({ error: 'Delegation exchange failed: ' + err.message })
      }
    }
  }

  /**
   * Build the req.delegation helper object with a fetch method.
   */
  private buildDelegationHelper(token: string) {
    return {
      token,
      fetch: async (url: string, fetchOpts?: { method?: string; body?: any; formData?: any }) => {
        const method = fetchOpts?.method || 'GET'
        const headers: Record<string, string> = {
          accept: 'application/json',
          Authorization: `Bearer ${token}`,
        }
        let bodyPayload: any = undefined
        if (fetchOpts?.formData) {
          bodyPayload = fetchOpts.formData
        } else if (fetchOpts?.body !== undefined) {
          headers['Content-Type'] = 'application/json'
          bodyPayload = JSON.stringify(fetchOpts.body)
        }
        const response = await this.mtlsFetch(url, {
          method,
          headers,
          ...(bodyPayload !== undefined ? { body: bodyPayload } : {}),
        })
        if (!response.ok) {
          throw new Error(`Admin API call failed: ${response.status} ${await response.text()}`)
        }
        if (response.status === 204) return undefined
        const text = await response.text()
        return text ? JSON.parse(text) : undefined
      },
    }
  }

  /**
   * Extract a session identifier from the access token for cache keying.
   */
  private extractSessionId(token: string): string {
    try {
      const payload = JSON.parse(Buffer.from(token.split('.')[1], 'base64url').toString())
      return payload.sid || payload.session_state || payload.sub || 'unknown'
    } catch {
      return 'unknown'
    }
  }
}
