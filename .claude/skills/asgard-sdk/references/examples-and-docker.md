# Example apps, running locally, and Docker

## The examples (`aspnet/Tide.Asgard.AspNetCore/Examples/`)

| Example | Shows | Frontend |
|---|---|---|
| `StandardResourceDelegation` | A DPoP-protected endpoint that fetches TideCloak's user list through delegation | plain JS |
| `LockingUserDataWithTide` | Adding a policy at first use and locking text with the Tide network | React |
| `ResourceToResourceDelegation` | A web app storing locked patient data in Postgres, and an analytics service it calls that unlocks and analyses it; fully dockerized | plain JS |

The end-to-end sample, `Tide.Asgard.AspNetCore.Example/` (beside `Examples/`), has `Hello`, `Policy/Create`,
`Account/Encrypt` and `Account/Decrypt`, each behind a button on a plain Vite page in `ClientApp/`. It is laid out
differently from the examples: `npm run build` in `ClientApp/` writes the gitignored `wwwroot/`, and building the server
does not build the page. Its policy id is a fresh GUID and its ciphers live in a static field, so after a restart you set
up the policy again and encrypt before you decrypt.

The example code is the user's own design - make the change asked for, keep it minimal, and keep it looking standard.

## How an example is laid out

They follow Microsoft's "React and ASP.NET Core" template:

- `X.Server/` - the ASP.NET project. Its csproj references the SDK and the client project
  (`<ReferenceOutputAssembly>false</ReferenceOutputAssembly>`), and has
  `<DefaultItemExcludes>$(DefaultItemExcludes);wwwroot\**</DefaultItemExcludes>`.
- `x.client/` - an `.esproj` (`Microsoft.VisualStudio.JavaScript.SDK`) that builds with Vite into the server's
  `wwwroot`. Building the server builds the client too.
- `.npmrc` has `ignore-scripts=true` and `vite.config.js` has `resolve.symlinks: false`, because `@tidecloak/js` is linked
  from the local checkout (`file:~/tidecloak-js/packages/tidecloak-js`).
- The `.slnx` is written by hand with just the two projects - `dotnet sln add` drags in the SDK's whole dependency tree.
- `Properties/launchSettings.json` serves on `http://localhost:3000`.
- Two adapter configs: the server's `appsettings.json` (`Keycloak` section = the confidential backend client) and the
  client's `tidecloak.json` (the public login client). Both hold live values - see the rules in SKILL.md.

## The local Tide stack

TideCloak runs in Docker as `tidecloakP` (ports 8080 and 8443), the ORKs on `localhost:1001`-`1005`, and Postgres as
`postgresP` (5432). The user-level `tide-stack`, `tide-build` and `tide-postgres` skills cover starting and inspecting it.

## Docker: `ResourceToResourceDelegation/compose.yaml`

```
echo "TIDECLOAK_REALM=<realm>" > .env   # .env is gitignored, so each developer creates it
docker compose up --build               # web app on http://localhost:3000
```

What's in it, and why:

- **`network`** - a small socat container both apps share. Inside a container `localhost` is the container, so it
  forwards `localhost:8080`, `8443` and `1001`-`1005` to the host, maps `{realm}.client.localhost` to itself (hence
  `TIDECLOAK_REALM`) and `hostgateway` to the host. The apps keep the same URLs as with `dotnet run`.
- **`webapp`, `analytics`** - built with the SDK folder as context, plus named build contexts for the sibling checkouts
  (`ork`, `tidecloak-js`, `heimdall`, `tide-js`), laid out under `/root` the same way as in the home folder so every
  relative link resolves. The Dockerfile deletes `prepare` from its copy of tidecloak-js's `package.json`, because npm
  runs it when first linking the package even with `ignore-scripts`.
- **Config and identity** - `appsettings.json` is mounted at run time, never baked in. Each app's enrolled identity lives
  in a named volume (`webapp-identity`, `analytics-identity`), so recreating a container doesn't enroll it again.
- **`postgres`** - not published on the host (5432 is usually taken). `Database/schema.sql` runs only on an empty
  volume; it also creates a read-only `analytics` login. Look inside with
  `docker compose exec postgres psql -U webapp`.

Careful: `docker compose down -v` deletes the identities **and** the database. Both apps then need fresh enrollment
tokens.

## Checking a page without TideCloak

Build the client in the scratchpad against a stand-in for tidecloak-js (a Vite `resolve.alias` for `@tidecloak/js`
pointing at a small mock that fakes `init`, `getName`, `getToken` and `fetch` answers), serve it, and look at it with the
Playwright tools. Screenshots have to be saved under `/home/sam/asgard/.playwright-mcp/` - delete them afterwards.
