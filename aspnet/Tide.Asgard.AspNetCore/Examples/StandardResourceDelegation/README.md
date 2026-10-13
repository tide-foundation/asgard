# Standard resource delegation from browser to a server - enabling mTLS bound access to Tidecloak on behalf of a user

A static page ([index.html](standardresourcedelegation.client/index.html)) logs the user in with tidecloak-js and calls the backend's one endpoint, `GET /users` ([Program.cs](StandardResourceDelegation.Server/Program.cs)). The endpoint demands a resource delegation, tidecloak-js answers it, and the backend then asks TideCloak for the user list on the user's behalf. The flow is drawn in [Asgard Resource Delegation](../../../../README.md#asgard-resource-delegation).

## Set up TideCloak

Follow the [prerequisites](../../../../GETTING_STARTED.md#prerequisites) for the realm and its two clients, then:

- name the public client `browser-login-page` (or change the name in `Program.cs`) and allow `http://localhost:3000` as its redirect URI and web origin
- give the user you will log in as the `view-users` and `query-users` roles of the `realm-management` client - without them TideCloak refuses the user list with `403`

## Add the two adapter configs

Download each from the Admin UI: Clients -> the client -> **Action** -> **Download adapter config**.

| Client | Goes in |
| --- | --- |
| backend (confidential) | [StandardResourceDelegation.Server/appsettings.json](StandardResourceDelegation.Server/appsettings.json), replacing the `Keycloak` object |
| public | [standardresourcedelegation.client/tidecloak.json](standardresourcedelegation.client/tidecloak.json), replacing the whole file |

## Run

> [!NOTE]
> **Running TideCloak locally?** Asgard contacts TideCloak for mTLS at `https://{realm}.client.localhost:8443`, a name your computer does not resolve by itself. Add it to `/etc/hosts`, using your realm's name, so the Asgard server can reach TideCloak:
>
> ```
> 127.0.0.1   {realm}.client.localhost
> ```

```bash
dotnet run --project StandardResourceDelegation.Server
```

The frontend is a JavaScript project the server project references, so building the server also installs the page's npm packages and builds it into the server's `wwwroot`. That needs Node.js, and a tidecloak-js checkout at `~/tidecloak-js`, which the page links to.

Open http://localhost:3000. The backend requests its certificate the first time it starts - until an admin approves that request in TideCloak, the page shows `503`.
