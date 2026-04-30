import { TideCloak as Tidecloak } from "@tidecloak/js";

const $ = (id) => document.getElementById(id);

const statusEl = $("auth-status");
const userInfoEl = $("user-info");
const resultEl = $("result");
const btnLogin = $("btn-login");
const btnLogout = $("btn-logout");
const btnCallApi = $("btn-call-api");

function log(msg) {
  resultEl.style.display = "block";
  resultEl.textContent += `[${new Date().toLocaleTimeString()}] ${msg}\n`;
}

function showAuthenticated(kc) {
  statusEl.textContent = "Authenticated (DPoP)";
  statusEl.style.color = "green";
  userInfoEl.style.display = "block";
  $("user-name").textContent = kc.tokenParsed?.preferred_username || kc.tokenParsed?.sub || "N/A";
  $("user-email").textContent = kc.tokenParsed?.email || "N/A";
  $("token-type").textContent = kc.tokenParsed ? "DPoP" : "Unknown";
  btnLogin.style.display = "none";
  btnLogout.style.display = "inline-block";
  btnCallApi.style.display = "inline-block";
}

function showUnauthenticated() {
  statusEl.textContent = "Not authenticated";
  statusEl.style.color = "red";
  userInfoEl.style.display = "none";
  btnLogin.style.display = "inline-block";
  btnLogout.style.display = "none";
  btnCallApi.style.display = "none";
}

async function callHelloEndpoint(kc) {
  log("Calling /Hello with DPoP via secureFetch...");

  try {
    // Re-login if token has expired (no refresh token available with check-sso)
    if (!kc.token) {
      log("Token expired, re-authenticating...");
      await kc.login();
      return;
    }

    const url = `${window.location.origin}/Hello`;
    const response = await kc.secureFetch(url, {
      headers: {
        Authorization: `Bearer ${kc.token}`,
      },
    });

    const status = response.status;
    const text = await response.text();

    if (response.ok) {
      log(`SUCCESS (${status}): ${text}`);
    } else {
      log(`FAILED (${status}): ${text}`);
      const wwwAuth = response.headers.get("WWW-Authenticate");
      if (wwwAuth) {
        log(`WWW-Authenticate: ${wwwAuth}`);
      }
    }
  } catch (err) {
    log(`ERROR: ${err.message}`);
  }
}

async function init() {
  const kc = new Tidecloak("keycloak.json");

  btnLogin.addEventListener("click", () => kc.login());
  btnLogout.addEventListener("click", () => kc.logout());
  btnCallApi.addEventListener("click", () => callHelloEndpoint(kc));

  kc.onTokenExpired = () => {
    log("Token expired, attempting refresh...");
    kc.updateToken(5).then(() => {
      log("Token refreshed successfully");
    }).catch(() => {
      log("Refresh failed, re-authenticating...");
      kc.login();
    });
  };

  try {
    statusEl.textContent = "Initializing Keycloak with DPoP...";

    const authenticated = await kc.init({
      onLoad: "login-required",
      checkLoginIframe: false,
     // useDPoP: { mode: "strict", alg: "EdDSA" },
    });

    if (authenticated) {
      log("Keycloak initialized - user is authenticated with DPoP");
      showAuthenticated(kc);
    } else {
      log("Keycloak initialized - user is not authenticated");
      showUnauthenticated();
    }
  } catch (err) {
    statusEl.textContent = `Init failed: ${err.message}`;
    statusEl.style.color = "red";
    btnLogin.style.display = "inline-block";
    log(`Keycloak init error: ${err.message}`);
  }
}

init();
