# Asgard

Asgard is a .NET **Cyber Immunity SDK**; authentication, authorization and authority for ASP.NET Core apps - built for **TideCloak**. With Asgard, your ASP.NET application can verify TideCloak logins, perform **ineffable locking** (encryption) of data under programmable policies, and securely exchange tokens between services.

Asgard requires the client side library **tidecloak-js**, which handles the browser authentication and approval flows.

The fastest way to see it working end-to-end is the [Tide.Asgard.AspNetCore.Example](aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.AspNetCore.Example/) project - the guides below mirror its setup.

## Guides to get into it
| Guide | Used for
| --- | --- |
| [USAGE](USAGE.md) | Using Asgard for user authentication, decentralized locking operations and policy management.
| [GETTING STARTED](GETTING_STARTED.md) | Integrating Asgard into a new ASP.NET project
| MIGRATION | Replacing another ASP.NET Authentication Provider with Asgard
| [EXTENSION](EXTENSION.md) | Extending core Asgard functions such as the AsgardCache

## How everything fits
### Asgard mTLS certificate auto-registration
```mermaid
sequenceDiagram
    box rgba(59, 130, 246, 0.15) Your application
        participant Browser@{ "type": "boundary", "alias": "Browser Client<br/>(using tidecloak-js)" }
        participant Backend@{ "type": "control", "alias": "ASP.NET Backend<br/>(using Asgard)" }
    end
    box rgba(139, 92, 246, 0.15)
        participant TideCloak@{ "type": "entity", "alias": "TideCloak IAM" }
    end
    box rgba(16, 185, 129, 0.15)
        participant TideNetwork@{ "type": "collections", "alias": "Tide Network" }
    end
    actor Admins

    Admins->>TideCloak: Download backend adapter config<br/>(includes one-time enrollment token)
    Admins->>Backend: Install adapter config in appsettings.json

    Note over Backend: Generate resource keys (identity + Tide)<br/>and certificate request
    Backend->>TideCloak: Request resource identity certificate<br/>(using one-time enrollment token)

    loop Until approved
        Backend->>TideCloak: Has resource identity been approved?
        TideCloak-->>Backend: Not yet
    end

    Admins->>TideCloak: Approve certificate request
    TideCloak->>TideNetwork: Sign resource identity certificate
    TideNetwork-->>TideCloak: Signed certificate

    Backend->>TideCloak: Has resource identity been approved?
    TideCloak-->>Backend: Yes - signed resource identity certificate<br/>+ tidecloak realm certificate
    Backend->>Backend: Verify both certificates,<br/>verify realm certificate key = Tide vendor key,<br/>use resource identity certificate
```
### Asgard Resource Delegation
```mermaid
sequenceDiagram
    box rgba(59, 130, 246, 0.15) Your application
        participant Browser@{ "type": "boundary", "alias": "Browser Client<br/>(using tidecloak-js)" }
        participant Backend@{ "type": "control", "alias": "ASP.NET Backend<br/>(using Asgard)" }
    end
    box rgba(246, 236, 92, 0.15)
        participant OtherResource@{ "type": "entity", "alias": "Files Resource" }
    end
    box rgba(139, 92, 246, 0.15)
        participant TideCloak@{ "type": "entity", "alias": "TideCloak IAM" }
    end
    box rgba(16, 185, 129, 0.15)
        participant TideNetwork@{ "type": "collections", "alias": "Tide Network" }
    end

    Browser->>Backend: Perform analysis on user files
    Note over Backend: Requires resource delegation<br/>approval from user
    Backend-->>Browser: Get delegation approval of my resource key
    Browser->>Browser: Sign approval with<br/>browser stored DPoP key

    opt Tide-secured delegation
        Browser->>Browser: Also sign approval with<br/>user's Tide session key
    end

    Browser->>Backend: Resource delegation approval
    Backend->>Backend: Verify approval
    Backend->>TideCloak: Exchange user token using delegation approval<br/>(over mTLS, using resource identity certificate)
    TideCloak->>TideNetwork: Sign exchanged token, verify approval
    TideNetwork-->>TideCloak: Signed token
    TideCloak-->>Backend: Exchanged token tied to resource mTLS<br/>+ doken for Tide Network requests (Tide-secured delegation only)
    Backend->>OtherResource: Request user files, authorized with<br/>resource mTLS bound token
```
### Encryption
```mermaid
sequenceDiagram
    actor User
    box rgba(59, 130, 246, 0.15) Your application
        participant Browser@{ "type": "boundary", "alias": "Browser Client<br/>(using tidecloak-js)" }
        participant Backend@{ "type": "control", "alias": "ASP.NET Backend<br/>(using Asgard)" }
    end
    box rgba(139, 92, 246, 0.15)
        participant TideCloak@{ "type": "entity", "alias": "TideCloak IAM" }
    end
    box rgba(16, 185, 129, 0.15)
        participant TideNetwork@{ "type": "collections", "alias": "Tide Network" }
    end

    User->>Browser: Login
    Browser->>TideCloak: Redirect
    TideCloak->>TideNetwork: Sign user token
    TideNetwork-->>TideCloak: Signed token
    TideCloak-->>Browser: User token tied to browser stored DPoP key

    Browser->>Backend: Encrypt my details
    Note over Browser,TideNetwork: Backend gets an mTLS bound token + doken through Tide-secured delegation<br/>(see Asgard Resource Delegation above)
    Backend->>TideCloak: Get encryption policy,<br/>authorized with mTLS bound token
    TideCloak-->>Backend: Policy
    Backend->>TideNetwork: Encrypt user details under policy, authorized with doken
    TideNetwork-->>Backend: Encrypted details
```


## 5 concepts to know

New to TideCloak? These are the only terms you need for these guides:

- **Realm** — your app's own space in TideCloak: its users, clients and policies.
- **Client** — a registration in the realm for each app that talks to TideCloak. A **public** client (e.g. your login page) runs in the browser and can't keep a secret; a **confidential** client (your backend) can prove its own identity — with Asgard, using a certificate signed by the Tide network.
- **Caller** — the logged-in user behind the current request. Their token arrives in the `Authorization` header of every call to your API.
- **Programmable policy** — a rule stored in TideCloak and enforced by the Tide network that defines who may lock or unlock what.
- **Ineffable locking** — encryption performed by the Tide network where the key never exists anywhere in full — no key material should be stored by your app.

For a deeper dive into policies and contracts, see the [Forseti engine docs](https://docs.tide.org/Core-Concepts/forseti-engine).

## Repository layout

The .NET solution lives at [aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.sln](aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.sln) and contains:

| Project | Purpose |
|---|---|
| [Tide.Asgard.AspNetCore.Authentication](aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.AspNetCore/) | Main SDK — service-collection extensions, Ed25519 helpers, locking, token exchange |
| [Tide.Asgard.Core](aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.Core/) | Cryptography primitives (Ed25519 / EdDSA), policy helpers |
| [Tide.Asgard.AspNetCore.DPoP](aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.AspNetCore.DPoP/) | DPoP (proof-of-possession) support |
| [Tide.Asgard.AspNetCore.Example](aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.AspNetCore.Example/) | End-to-end working sample |


## License

This project is dual-licensed:

- **ASP.NET Core authentication libraries** (`aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.AspNetCore/`): Derived from the [Auth0 ASP.NET Core Authentication API](https://github.com/auth0/aspnetcore-api), which is licensed under the [Apache License 2.0](aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.AspNetCore/LICENSE-APACHE-2.0). Modifications by Tide Foundation Limited are subject to both the Apache 2.0 license (for the derived portions) and the Tide Community Open Code License (for new additions). See the [NOTICE](aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.AspNetCore/NOTICE) file for full attribution details.
- **Core cryptography libraries** (`aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.Core/`): Derived from [ScottBrady.IdentityModel](https://github.com/scottbrady91/IdentityModel), which is licensed under the [Apache License 2.0](aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.Core/LICENSE-APACHE-2.0). Modifications by Tide Foundation Limited are subject to both the Apache 2.0 license (for the derived portions) and the Tide Community Open Code License (for new additions). See the [NOTICE](aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.Core/NOTICE) file for full attribution details.
