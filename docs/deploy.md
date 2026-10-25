# Deploy: photon

## Building the image

**By the release workflow.** The image needs nothing beyond the repository: the Fusion Shared-mode
host is our own code (`OpenPak.Server.LoadBalancing/Fusion`), with no retail assemblies to stage.
`release.yml` builds and pushes it, so a `v*.*.*` tag is the deploy, like every other OpenPak service.

A hand build from a workstation works the same way:

```sh
podman build --platform linux/amd64,linux/arm64 --manifest ghcr.io/openpak/photon:build .
podman manifest push --all ghcr.io/openpak/photon:build docker://ghcr.io/openpak/photon:latest
ssh <deploy host> 'podman pull ghcr.io/openpak/photon:latest &&
                   systemctl --user restart openpak-photon'   # drops live games
```

## Building & running (CLI)

Requires the **.NET 8 SDK**. No game files are needed to build or test the server:

```bash
dotnet build server/OpenPak.Server.sln          # build the server
dotnet test  server/OpenPak.Server.sln          # run the test suite
dotnet run --project server/OpenPak.Server.Host 127.0.0.1   # run, advertising 127.0.0.1
```

On first run the server writes a documented `openpak.server.json` next to the binary, applies the
database schema, prints a one-time admin bootstrap code, and starts the Name/Master/Game listeners.
Rooms can be served from region nodes in other countries; see [docs/federation.md](federation.md).
Configure it via that file or `OPENPAK_*` environment variables — see
[`docs/configuration.md`](configuration.md). Stop with Ctrl-C (graceful shutdown).

## Building & running (Visual Studio)

Requires **Visual Studio 2022 (17.8+)** with the **.NET 8 SDK**. A local copy of Black Ice is only
needed for the full interop **oracle** tests and the BepInEx plugins; the server itself builds and
tests without it.

1. Open **`OpenPak.sln`** at the repo root — it contains the server and the client plugins.
2. Set **`OpenPak.Server.Host`** as the startup project (right-click → *Set as Startup Project*).
3. Press **F5**. Launch profiles are provided (Run/Debug dropdown):
   - *OpenPak.Server (local LAN)* — advertises `127.0.0.1` (same-PC testing).
   - *OpenPak.Server (advertise this PC on LAN)* — edit the IP in the profile to this machine's
     LAN address so other PCs can connect.
   - *OpenPak.Server (require Name Server token)* — disables anonymous LAN auth.

Run tests via **Test → Run All Tests**. (With the game's `Photon3Unity3D.dll` present, the full
interop oracle suite runs too; without it, those tests are skipped and the rest still run.)
