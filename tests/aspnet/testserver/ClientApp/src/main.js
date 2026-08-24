import { TideCloak as Tidecloak } from "@tidecloak/js";

/**
 * The whole SPA: authenticate with DPoP, then call the one endpoint.
 *
 * Unlike the example app this does NOT use onLoad: "login-required" — the page
 * settles into a stable unauthenticated state with a Login button instead of
 * redirecting on load, so a test can assert "not authenticated" before driving
 * the login itself.
 *
 * Element ids are the contract with the test; the markup around them is not.
 */

const $ = (id) => document.getElementById(id);

const statusEl = $("auth-status");
const userEl = $("user-name");
const resultEl = $("result");
const btnLogin = $("btn-login");
const btnLogout = $("btn-logout");
const btnCallApi = $("btn-call-api");
const btnExchange = $("btn-exchange");

function log(msg) {
  resultEl.textContent += `${msg}\n`;
}

function show(authenticated, kc) {
  statusEl.textContent = authenticated ? "authenticated" : "unauthenticated";
  userEl.textContent = authenticated
    ? (kc.tokenParsed?.preferred_username ?? kc.tokenParsed?.sub ?? "-")
    : "-";
  btnLogin.hidden = authenticated;
  btnLogout.hidden = !authenticated;
  btnCallApi.hidden = !authenticated;
  btnExchange.hidden = !authenticated;
}

/**
 * @param path  endpoint to hit
 * @param label prefix for the log line, so the test can match on it
 *
 * secureFetch attaches the DPoP proof; a bare Authorization header would be
 * rejected, since the server runs DPoPModes.Required. For /Hello/exchange it
 * ALSO answers the server's delegation_required challenge and retries with a
 * DPoP-Resource-Delegation header — all in-browser, no enclave popup.
 */
async function call(kc, path, label) {
  try {
    const res = await kc.secureFetch(`${window.location.origin}${path}`, {
      headers: { Authorization: `Bearer ${kc.token}` },
    });
    log(`${label} ${res.status} ${await res.text()}`);
    const wwwAuth = res.headers.get("WWW-Authenticate");
    if (wwwAuth) log(`${label} www-authenticate ${wwwAuth}`);
  } catch (err) {
    log(`${label} error ${err.message}`);
  }
}

async function init() {
  // Served by the server from the adaptor directory, not from wwwroot.
  const kc = new Tidecloak("keycloak.json");

  btnLogin.addEventListener("click", () => kc.login());
  btnLogout.addEventListener("click", () => kc.logout());
  btnCallApi.addEventListener("click", () => call(kc, "/Hello", "hello"));
  btnExchange.addEventListener("click", () => call(kc, "/Hello/exchange", "exchange"));

  try {
    const authenticated = await kc.init({
      onLoad: "check-sso",
      silentCheckSsoRedirectUri: `${window.location.origin}/silent-check-sso.html`,
      checkLoginIframe: false,
      useDPoP: { mode: "strict", alg: "EdDSA" },
    });
    log(`init ok authenticated=${authenticated}`);
    show(authenticated, kc);
  } catch (err) {
    statusEl.textContent = "init-failed";
    btnLogin.hidden = false;
    log(`init error ${err.message}`);
  }
}

init();
