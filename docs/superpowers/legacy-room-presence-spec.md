# earlier room presence bridge — specification

## Goal

Make an Outbound room hosted through this Photon server joinable from the
earlier friend feed for both emulator and real Switch clients.

## Behaviour

- On NintendoSwitch authentication, recover the embedded `openpak` credential
  from the BAAS JWT without logging it.
- Keep that credential in memory only, keyed by a non-secret stable identity.
- When that identity creates a six-character Outbound room, POST the room to
  the configured earlier account service using the credential as bearer auth.
- Do not change Steam authentication or room behaviour when the integration is
  unconfigured.
- Never persist or log BAAS/nx2 credentials.

## Configuration

`OPENPAK_ACCOUNT_URL` (or legacy `OPENPAK_ACCOUNT_URL`) enables publishing (for example
`http://127.0.0.1:8099`). Unset means disabled.
