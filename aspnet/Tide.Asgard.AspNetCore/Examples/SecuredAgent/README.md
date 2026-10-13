# Allow an agentic service to access locked user data on behalf of a user.

> [!NOTE]
> **Running TideCloak locally?** Asgard contacts TideCloak for mTLS at `https://{realm}.client.localhost:8443`, a name your computer does not resolve by itself. Add it to `/etc/hosts`, using your realm's name, so the Asgard server can reach TideCloak:
>
> ```
> 127.0.0.1   {realm}.client.localhost
> ```
