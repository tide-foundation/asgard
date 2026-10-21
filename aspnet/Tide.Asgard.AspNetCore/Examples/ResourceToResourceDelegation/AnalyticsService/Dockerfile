# The analytics service. Build it through the example's compose.yaml, which gives this build the Asgard SDK folder as
# its context, and the ork checkout the SDK links to as a named context.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

# The SDK links to the ork checkout beside this repo (../project/ork). Lay it out the same way in root's home, so
# the link still resolves.
WORKDIR /root
COPY --from=ork Cryptide/Cryptide project/ork/Cryptide/Cryptide/
COPY --from=ork Ork.Models project/ork/Ork.Models/
COPY --from=ork Ork.Clients project/ork/Ork.Clients/
COPY Tide.Asgard.Core asgard/aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.Core/
COPY Tide.Asgard.AspNetCore asgard/aspnet/Tide.Asgard.AspNetCore/Tide.Asgard.AspNetCore/
COPY Examples/ResourceToResourceDelegation/AnalyticsService asgard/aspnet/Tide.Asgard.AspNetCore/Examples/ResourceToResourceDelegation/AnalyticsService/
# the ork checkout's build output is your machine's
RUN find project/ork -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +

RUN dotnet publish asgard/aspnet/Tide.Asgard.AspNetCore/Examples/ResourceToResourceDelegation/AnalyticsService/AnalyticsService.csproj -c Release -o /app/publish /p:UseAppHost=false

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

# https only, on the port dotnet run uses and the web application calls - SupportMTLSBoundTokens serves mTLS on it.
# The base image's http port is cleared: the two apps share one network, where 8080 is the forwarder's.
ENV ASPNETCORE_HTTP_PORTS="" \
    ASPNETCORE_HTTPS_PORTS=3001
EXPOSE 3001

USER $APP_UID
ENTRYPOINT ["dotnet", "AnalyticsService.dll"]
