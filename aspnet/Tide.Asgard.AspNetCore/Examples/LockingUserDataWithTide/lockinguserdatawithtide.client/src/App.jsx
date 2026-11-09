import { useEffect, useState } from "react";
import { IAMService } from "@tidecloak/js";
import config from "../tidecloak.json"; // the public client's adapter config

// keep the token fresh while the page stays open - an admin's approval can take longer than a token lasts
IAMService.on("tokenExpired", () => IAMService.updateToken().catch(() => IAMService.doLogin()));

// Log in with a DPoP-bound token. Started here rather than in the component, so it happens exactly once.
const login = IAMService.init(config, {
  onLoad: "login-required",
  checkLoginIframe: false,
  setupRequestEnclave: false,
  dpopConfig: { mode: "strict", alg: "EdDSA" },
});

export default function App() {
  const [status, setStatus] = useState("Logging in...");
  const [busy, setBusy] = useState(true); // until logged in, and during each call
  const [text, setText] = useState("");
  const [locked, setLocked] = useState(null); // the text that was sent, with what the backend returned for it
  const [alert, setAlert] = useState(null); // { title, detail, error } - why the last lock did not happen

  useEffect(() => {
    login.then(
      () => {
        setStatus(`Logged in as ${IAMService.getName()}`);
        setBusy(false);
      },
      (error) => setStatus(error.message),
    );
  }, []);

  async function lock(event) {
    event.preventDefault();
    setStatus("Locking...");
    setLocked(null);
    setAlert(null);
    setBusy(true);
    try {
      // IAMService.fetch sends the token with a DPoP proof. The backend first answers 401, asking for a
      // resource delegation - tidecloak-js signs one and retries, all inside this call.
      const response = await IAMService.fetch("/lock", {
        method: "POST",
        headers: { Authorization: `Bearer ${IAMService.getToken()}`, "Content-Type": "application/json" },
        body: JSON.stringify({ text }),
      });

      if (response.status === 202) {
        // the backend has only just added the policy to TideCloak, and an admin still has to approve it
        setAlert({ title: "Not locked yet - the policy needs approval", detail: await response.text() });
        setStatus("Nothing was locked");
      } else if (response.ok) {
        setLocked({ text, ...(await response.json()) });
        setStatus(`Locked on behalf of ${IAMService.getName()}`);
      } else {
        // a refusal is shown as it came back, with Asgard's error code when there is one
        const code = response.headers.get("Asgard-Exception");
        setAlert({
          error: true,
          title: `${response.status} ${response.statusText}${code ? ` - ${code}` : ""}`,
          detail:
            code === "ResourceIdentityNotRegistered"
              ? "This backend's certificate request is still waiting for an admin's approval in TideCloak. Once it is approved, the backend collects the certificate within a minute - then lock again."
              : await response.text(),
        });
        setStatus("Nothing was locked");
      }
    } catch (error) {
      setAlert({ error: true, title: "The request failed", detail: error.message });
      setStatus("Nothing was locked");
    }
    setBusy(false);
  }

  return (
    <>
      <h1>Lock user data with Tide</h1>
      <p id="status">{status}</p>

      <p>
        The backend asks the Tide Network to lock your text under a policy, and returns the cipher. The first
        lock also adds that policy to TideCloak.
      </p>
      <form onSubmit={lock}>
        <input value={text} onChange={(event) => setText(event.target.value)} placeholder="Something to lock" required />
        <button disabled={busy}>Lock</button>
      </form>

      {alert && (
        <p role="alert" className={alert.error ? "error" : undefined}>
          <strong>{alert.title}</strong>
          {alert.detail}
        </p>
      )}

      {locked && (
        <>
          <dl>
            <dt>What you typed - kept only by this page</dt>
            <dd>{locked.text}</dd>
            <dt>What the backend returned - {atob(locked.cipher).length} bytes of locked data, shown as base64</dt>
            <dd><code>{locked.cipher}</code></dd>
          </dl>
          <p>
            The backend stored nothing and never held the key: the Tide Network locked your text under the
            policy <code>{locked.policy}</code>, and this cipher is all that came back.
          </p>
        </>
      )}
    </>
  );
}
