# Lock user data on an Asgard protected server, using programmable policies.

> [!NOTE]
> **Running TideCloak locally?** Asgard contacts TideCloak for mTLS at `https://{realm}.client.localhost:8443`, a name your computer does not resolve by itself. Add it to `/etc/hosts`, using your realm's name, so the Asgard server can reach TideCloak:
>
> ```
> 127.0.0.1   {realm}.client.localhost
> ```

A React page ([App.jsx](lockinguserdatawithtide.client/src/App.jsx)) logs the user in with tidecloak-js and calls the backend's one endpoint, `POST /lock` ([Program.cs](LockingUserDataWithTide.Server/Program.cs)). The backend creates the policy when it starts, and adds it to TideCloak the first time something is locked. `POST /lock` then locks the text the user typed under that policy and returns the cipher - the Tide Network does the locking, so the backend never holds the key, and it stores nothing. The flow is drawn in [Encryption](../../../../README.md#encryption). To read locked data back, see [Unlock data](../../../../USAGE.md#unlock-data) in the usage guide.

## Set up TideCloak

Follow the [prerequisites](../../../../GETTING_STARTED.md#prerequisites) for the realm and its two clients, then name the public client `browser-login-page` (or change the name in `Program.cs`) and allow `http://localhost:3000` as its redirect URI and web origin.

## Add the two adapter configs

Download each from the Admin UI: Clients -> the client -> **Action** -> **Download adapter config**.

| Client | Goes in |
| --- | --- |
| backend (confidential) | [LockingUserDataWithTide.Server/appsettings.json](LockingUserDataWithTide.Server/appsettings.json), replacing the `Keycloak` object |
| public | [lockinguserdatawithtide.client/tidecloak.json](lockinguserdatawithtide.client/tidecloak.json), replacing the whole file |

Both files hold a placeholder until you do.

## Run

```bash
dotnet run --project LockingUserDataWithTide.Server
```

The frontend is a JavaScript project the server project references, so building the server also installs the page's npm packages and builds it into the server's `wwwroot`. That needs Node.js, and a tidecloak-js checkout at `~/tidecloak-js`, which the page links to.

Open http://localhost:3000. The backend requests its certificate the first time it starts - until an admin approves that request in TideCloak, **Lock** ends in `503`.

The first **Lock** adds the policy to TideCloak. If the realm requires quorum approval, the page shows a change request id instead of a cipher, and an admin must approve that change request in TideCloak before **Lock** works.
