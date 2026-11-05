# Account erasure

`DELETE /internal/accounts/{account_id}/data` on the health listener (`Server.HealthPort`, default 8080, container
network only), `Authorization: Bearer $ACCOUNT_ERASE_KEY`. 503 while the key is unset, 401 on a wrong key, 400 when
the id is not a UUID. Answer: `{"service":"photon","rows":N}`; a second call answers `rows` 0.

The server asks the account core for the account's `switch` links (`ACCOUNT_GRPC_ADDR`, default
`openpak-account:20001`, key `ACCOUNT_INTERNAL_KEY` or `OPENPAK_INTERNAL_KEY`). The BAAS user id is the only
per-account key Photon holds, in the rewards state (`rewards_state`): the userId to subject cache (`subjects`), the
session clock (`clock`) and unsent events (`outbox`). Those entries are removed, in the running objects and in the
table, together with the in-memory credential, country and tester caches of the userIds mapped to the account.

Kept: room ids and the `Accounts`/`Profiles` tables (since v0.19.0 they hold no name: `DisplayName` was dropped). The latter are keyed by Steam id for Steam peers (console peers are
`openpak:` ids with no row), carry no OpenPak account reference and are not tied to an account that could be erased.
