# Ports — photon (trimmed)

> Trimmed copy for this repository; the canonical document lives at
> `Openpak/ports.md` and governs. Last synchronised 2026-09-15.

One block per concern, nothing below 20000, nothing at or above 27000 (Photon-Reference and
Steam live there). Every service reads its listeners from `<SVC>_HTTP_ADDR`, `<SVC>_GRPC_ADDR`,
`<SVC>_METRICS_ADDR`; the values below are the defaults and the local-run convention. Each
service owns a block of ten: +0 HTTP, +1 gRPC, +2 metrics/pprof, +3..+9 spare.

## Photon (UDP)

| Port(s) | Service |
| --- | --- |
| 22000 / 22001 / 22002 | Photon-compatible NameServer / Master / GameServer (UDP), primary |
| 22002 / 22012 udp | Photon GameServer (Game role only) |
| 22010 / 22011 / 22012 | same, alternate ports (the client falls back to these) |

## Health

| Service | Endpoint |
| --- | --- |
| Photon | `:8080/health`, `:8080/players` (live room membership) |
Photon's listeners are UDP and cannot be probed, so it also serves plain HTTP on **8080**

Other services' rows live in the canonical ports.md.
