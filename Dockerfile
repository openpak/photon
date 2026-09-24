# The Fusion Shared-mode host links Photon Fusion's own assemblies, which are NOT
# redistributable and therefore NOT in the repo: server/fusion-libs/ must exist in the
# build context (see docs/fusion-shared-server.md). The release workflow copies them in from
# the self-hosted runner's own copy and fails when that is missing. A build without them still
# succeeds — you just get the solo-host path, no Fusion relay.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY . .
RUN dotnet publish server/OpenPak.Server.Host -c Release -r linux-$TARGETARCH \
        --self-contained false -o /out

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
# No RUN in this stage: the image assembles by copy alone, so cross-arch builds need no emulation.
COPY --from=build --chown=65532:65532 /out /app
USER 65532:65532
ENTRYPOINT ["dotnet", "/app/OpenPak.Server.Host.dll"]
