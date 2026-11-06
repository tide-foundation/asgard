# The web application: the ASP.NET server and the frontend it serves. Build it through the example's compose.yaml,
# which gives this build the Asgard SDK folder as its context, and the local checkouts it links to as named contexts.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

# the frontend project builds with npm
RUN apt-get update \
    && apt-get install -y curl \
    && curl -fsSL https://deb.nodesource.com/setup_22.x | bash - \
    && apt-get install -y nodejs

# The SDK and the frontend link to checkouts beside this repo: the SDK to ../project/ork, the frontend to
# ~/tidecloak-js, which links ~/heimdall, which links ~/project/tide-js. Lay them out the same way in root's home,
# so every link still resolves.
WORKDIR /root
COPY --from=ork Cryptide/Cryptide project/ork/Cryptide/Cryptide/
COPY --from=ork Ork.Models project/ork/Ork.Models/
COPY --from=ork Ork.Clients project/ork/Ork.Clients/
COPY --from=tide-js . project/tide-js/
COPY --from=heimdall . heimdall/
COPY --from=tidecloak-js packages/tidecloak-js tidecloak-js/packages/tidecloak-js/
COPY Tide.Asgard.Core asgard/aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.Core/
COPY Tide.Asgard.AspNetCore asgard/aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.AspNetCore/
COPY Examples/ResourceToResourceDelegation/WebApplication asgard/aspnet/Tide.Asgard.AspNetCore/Examples/ResourceToResourceDelegation/WebApplication/
# the ork checkout's build output is your machine's
RUN find project/ork -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +
# The tidecloak-js checkout is built already. npm runs a package's prepare script when it first links it, even with
# ignore-scripts set, so take it out of this copy.
RUN cd tidecloak-js/packages/tidecloak-js && npm pkg delete scripts.prepare

# publishing the server builds the frontend into its wwwroot too
RUN dotnet publish asgard/aspnet/Tide.Asgard.AspNetCore/Examples/ResourceToResourceDelegation/WebApplication/WebApp.Server/WebApp.Server.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .

# Asgard keeps this resource's identity here: its keys, certificate request and certificates. Mount a volume on it,
# or the container enrolls as a new resource every time it is recreated.
RUN mkdir identity && chown $APP_UID identity
ENV Asgard__identity_key_path=/app/identity/resource.key \
    Asgard__tide_key_path=/app/identity/tide.key \
    Asgard__certificate_request_path=/app/identity/resource.csr \
    Asgard__certificate_path=/app/identity/resource.crt \
    Asgard__root_ca_path=/app/identity/root.crt

# the port dotnet run uses
ENV ASPNETCORE_HTTP_PORTS=3000
EXPOSE 3000

USER $APP_UID
ENTRYPOINT ["dotnet", "WebApp.Server.dll"]
