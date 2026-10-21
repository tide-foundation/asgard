# Allow an agentic service to access locked user data on behalf of a user.

> [!NOTE]
> **Running TideCloak locally?** Asgard contacts TideCloak for mTLS at `https://{realm}.client.localhost:8443`, a name your computer does not resolve by itself. Add it to `/etc/hosts`, using your realm's name, so the Asgard server can reach TideCloak:
>
> ```
> 127.0.0.1   {realm}.client.localhost
> ```
>
> With Docker, compose does this for you - see below.

## Run it with Docker

[compose.yaml](compose.yaml) runs the example in containers: the web application, the analytics service, and a Postgres database for the web application's encrypted patient records and locking policy. TideCloak is not part of it - the apps use the one already running on your machine.

1. Put each app's adapter config in its `appsettings.json`. Compose mounts them into the containers; they are not built into the images.
2. Give compose your realm's name, in a `.env` file beside `compose.yaml`:

   ```
   TIDECLOAK_REALM=myrealm
   ```

3. Start it, then open http://localhost:3000:

   ```
   docker compose up --build
   ```

Good to know:

- **The images build from source, including your local checkouts.** The SDK links to `project/ork` beside this repo, and the frontend to `tidecloak-js`, which links `heimdall` and `project/tide-js`. Compose passes these to the build, so all four need to sit in the same folder as this repo - your home folder, as `~/tidecloak-js` in the frontend's `package.json` expects.
- **`localhost` means what it does with `dotnet run`.** The apps share the network of a small forwarder container, which sends `localhost:8080` and `{realm}.client.localhost:8443` on to TideCloak on your machine, and `localhost:1001`-`1005` on to the Tide network's ORKs. `hostgateway`, the name TideCloak gives the ORKs, also leads to your machine. The analytics service is reachable only from the web application.
- **Each container enrolls as a new resource,** even if you already enrolled the app with `dotnet run`. On first start it requests a certificate with the `enrollment_token` in its adapter config (download a fresh config if that token was used) and waits for an admin to approve it. The identity is kept in a volume, so later starts reuse it. `docker compose down -v` deletes it, along with the database.
- **The database** gets its tables from [Database/schema.sql](Database/schema.sql) on first start. Both apps receive a connection string as `ConnectionStrings:DefaultConnection`: the web application's owns the database, and the analytics service's can only read the `patients` table. The database isn't published on your machine; to look inside, run `docker compose exec postgres psql -U webapp`.
