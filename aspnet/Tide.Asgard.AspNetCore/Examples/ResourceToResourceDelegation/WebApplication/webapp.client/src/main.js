import { IAMService } from "@tidecloak/js";
import config from "../tidecloak.json"; // the public client's adapter config

const status = document.querySelector("#status");
const alert = document.querySelector("[role=alert]");
const result = document.querySelector("#result");
const buttons = document.querySelectorAll("button");
const patientId = document.querySelector("[name=patientId]");

// why something did not happen - orange while it waits on an admin, red when it failed
function showAlert(title, detail, error = false) {
  alert.replaceChildren(Object.assign(document.createElement("strong"), { textContent: title }), detail);
  alert.classList.toggle("error", error);
  alert.hidden = false;
}

// a refusal is shown as it came back, with Asgard's error code when there is one
async function showRefusal(response) {
  const code = response.headers.get("Asgard-Exception");
  showAlert(
    `${response.status} ${response.statusText}${code ? ` - ${code}` : ""}`,
    code === "ResourceIdentityNotRegistered"
      ? "This backend's certificate request is still waiting for an admin's approval in TideCloak. Once it is approved, the backend collects the certificate within a minute - then reload this page."
      : await response.text(),
    true,
  );
}

function showResult(title, detail) {
  result.querySelector("dt").textContent = title;
  result.querySelector("dd").textContent = detail;
  result.hidden = false;
}

// Calls the backend on behalf of the logged-in user. IAMService.fetch sends the token with a DPoP proof. The backend
// first answers 401, asking for a resource delegation - tidecloak-js signs one and retries, all inside this call.
function call(path, options = {}) {
  return IAMService.fetch(path, {
    ...options,
    headers: { Authorization: `Bearer ${IAMService.getToken()}`, ...options.headers },
  });
}

// one call at a time: clears the last outcome, and keeps the buttons disabled until the call is done
async function run(action) {
  alert.hidden = true;
  result.hidden = true;
  buttons.forEach((button) => (button.disabled = true));
  try {
    await action();
  } catch (error) {
    showAlert("The request failed", error.message, true);
  }
  buttons.forEach((button) => (button.disabled = false));
}

// keep the token fresh while the page stays open - an admin's approval can take longer than a token lasts
IAMService.on("tokenExpired", () => IAMService.updateToken().catch(() => IAMService.doLogin()));

async function main() {
  // log in with a DPoP-bound token
  await IAMService.init(config, {
    onLoad: "login-required",
    checkLoginIframe: false,
    setupRequestEnclave: false,
    dpopConfig: { mode: "strict", alg: "EdDSA" },
  });
  const loggedIn = `Logged in as ${IAMService.getName()}`;
  status.textContent = `${loggedIn} - submitting the locking policy...`;

  await run(async () => {
    status.textContent = `${loggedIn} - the locking policy was not submitted`; // until the backend says otherwise

    // ask the backend to add its locking policy to TideCloak
    const response = await call("/server/setup");
    if (response.status === 202) {
      // TideCloak has the policy, but an admin still has to approve it
      showAlert("The locking policy needs approval", await response.text());
      status.textContent = `${loggedIn} - the locking policy is waiting for approval`;
    } else if (response.ok) {
      status.textContent = `${loggedIn} - the locking policy is live`;
    } else {
      await showRefusal(response);
    }
  });
}

document.querySelector("#add-patient").addEventListener("submit", (event) => {
  event.preventDefault();
  const form = event.target;
  const patient = new FormData(form);
  run(async () => {
    const response = await call("/server/AddPatient", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        name: patient.get("name"),
        age: Number(patient.get("age")),
        address: patient.get("address"),
        medicalDetails: patient.get("medicalDetails"),
      }),
    });
    if (!response.ok) return showRefusal(response);

    patientId.value = await response.json(); // ready to ask about
    form.reset();
    showResult("Patient added - the backend stored only the ciphers, under this id", patientId.value);
  });
});

document.querySelector("#summaries").addEventListener("submit", (event) => {
  event.preventDefault();
  const { value: endpoint, textContent: summary } = event.submitter; // the button pressed
  run(async () => {
    const response = await call(`/server/${endpoint}?patientId=${encodeURIComponent(patientId.value)}`);
    if (!response.ok) return showRefusal(response);
    showResult(`${summary} from the analytics service`, await response.text());
  });
});

document.querySelector("#average-age").addEventListener("click", () =>
  run(async () => {
    const response = await call("/server/AverageAge");
    if (!response.ok) return showRefusal(response);
    showResult("Average age of all patients, from the analytics service", await response.text());
  }),
);

main().catch((error) => (status.textContent = error.message));
