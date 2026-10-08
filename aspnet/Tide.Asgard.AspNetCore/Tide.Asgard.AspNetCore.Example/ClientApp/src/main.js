import { IAMService } from "@tidecloak/js";
import config from "../public/keycloak.json";


const $ = (id) => document.getElementById(id);

const statusEl = $("auth-status");
const userInfoEl = $("user-info");
const resultEl = $("result");
const btnLogin = $("btn-login");
const btnPolicySetup = $("btn-setup-policy");
const btnLogout = $("btn-logout");
const btnCallApi = $("btn-call-api");
const btnEncryptAccount = $("btn-encrypt-account");

function log(msg) {
  resultEl.style.display = "block";
  resultEl.textContent += `[${new Date().toLocaleTimeString()}] ${msg}\n`;
}

function showAuthenticated() {
  statusEl.textContent = "Authenticated (DPoP)";
  statusEl.style.color = "green";
  userInfoEl.style.display = "block";
  $("user-name").textContent = IAMService.getName() || IAMService.getClaim("sub") || "N/A";
  $("user-email").textContent = IAMService.getClaim("email") || "N/A";
  $("token-type").textContent = IAMService.getToken() ? "DPoP" : "Unknown";
  btnLogin.style.display = "none";
  btnLogout.style.display = "inline-block";
  btnCallApi.style.display = "inline-block";
  btnPolicySetup.style.display = "inline-block";
  btnEncryptAccount.style.display = "inline-block";
}

function showUnauthenticated() {
  statusEl.textContent = "Not authenticated";
  statusEl.style.color = "red";
  userInfoEl.style.display = "none";
  btnLogin.style.display = "inline-block";
  btnLogout.style.display = "none";
  btnCallApi.style.display = "none";
  btnPolicySetup.style.display = "none";
  btnEncryptAccount.style.display = "none";
}

async function setupPolicy() {
    log("Setting up policy...");

    const response = await IAMService.fetch('http://localhost:3000/policy/create', {
        headers: {
            Authorization: `Bearer ${IAMService.getToken()}`,
        },
    });

    const status = response.status;
    const text = await response.text();
    log(text);
}

async function callHelloEndpoint() {
  log("Calling /Hello with DPoP via IAMService.fetch...");

  try {
    // Re-login if token has expired (no refresh token available with check-sso)
    if (!IAMService.getToken()) {
      log("Token expired, re-authenticating...");
      await IAMService.doLogin();
      return;
    }

    const url = `${window.location.origin}/Hello`;
    const response = await IAMService.fetch(url, {
      headers: {
        Authorization: `Bearer ${IAMService.getToken()}`,
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

async function encryptAccount() {
  log("Calling /Account (EncryptAccount) with DPoP via IAMService.fetch...");

  try {
    const url = `${window.location.origin}/Account/Encrypt`;
    const response = await IAMService.fetch(url, {
      headers: {
        Authorization: `Bearer ${IAMService.getToken()}`,
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
  btnLogin.addEventListener("click", () => IAMService.doLogin());
  btnLogout.addEventListener("click", () => IAMService.doLogout());
  btnCallApi.addEventListener("click", () => callHelloEndpoint());
  btnPolicySetup.addEventListener("click", () => setupPolicy());
  btnEncryptAccount.addEventListener("click", () => encryptAccount());

  IAMService.on("tokenExpired", () => {
    log("Token expired, attempting refresh...");
    IAMService.updateToken().then(() => {
      log("Token refreshed successfully");
    }).catch(() => {
      log("Refresh failed, re-authenticating...");
      IAMService.doLogin();
    });
  });

  try {
    statusEl.textContent = "Initializing Keycloak with DPoP...";

    const authenticated = await IAMService.init(config, {
      onLoad: "login-required",
      checkLoginIframe: false,
      setupRequestEnclave: false,
      dpopConfig: { mode: "strict", alg: "EdDSA" },
    });

    if (authenticated) {
      log("Keycloak initialized - user is authenticated with DPoP");
      showAuthenticated();
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
