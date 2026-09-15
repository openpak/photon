# The OpenPak look

> Trimmed copy for this repository; the canonical document lives at
> `Openpak/docs/design.md` and governs. Last synchronised 2026-09-15.

One design across three surfaces: the website, the console link pages, and the phone app. They
are built with completely different tools and must still read as one product, so what follows
is the part that has to stay in step — not a style guide anybody has to admire.

## What we deliberately do not serve (2026-09-08)

A console asks a lot of servers. Answering one badly is worse than not
redirecting it at all, so each is a decision rather than an omission.

| Service | Decision |
| --- | --- |
| Telemetry (`*.dragons`/`ndas` play reports, `receive-lp1`) | **Never.** Nothing about how somebody plays needs to leave their house. |
| System updates (`sun`, `atumn`, `aqua`, `superfly`) | **Never.** Firmware is Nintendo's to sign; a console pointed at us for updates is a console we could brick. |
| eShop / Atum (title content: NCA files, icons, updates) | **Later, for homebrew.** Atum is the file server behind the shop, edge-token gated. Hosting homebrew through it is wanted; today it is out of scope. |
| ZNC (`api-lp1.znc`) | **Not yet.** It is the Nintendo Switch Online phone app's API — and the console's **voice chat** path. Our companion app speaks our own API instead; voice chat would need this. |
| Eagle relay | **Not yet.** A handful of titles relay instead of peer-to-peer: Tetris 99, Super Mario Bros. 35. Matchmaking stays NEX; the session hands out an eagle URL and token. |
| NAT check, connection test | Not served; a console that cannot check its NAT falls back sensibly. |

Served today: device auth (dauth/aauth/dcert/acert), dragons eLicenses, BAAS
users and friends, Nintendo Accounts, Penne push, Vermillion, Beach, capi
memberships, NPLN game servers, BCAT news (empty), OLSC saves (served by `saves` through the adapter; console acceptance of the key seed package is M0, untested).

## Rules that keep costing us when broken

1. **Mobile first.** The narrow layout is the design; wider screens add to it. The site was
   desktop-first with patches, which is why fixing one page never fixed the next.
2. **No font under 16px on an input.** Below that iOS zooms on focus and leaves the page
   scrolled sideways — a layout bug the reader cannot undo.
3. **44px touch targets**, 60px on a console where a stick is doing the aiming.
4. **Long strings wrap** — friend codes, title ids, tokens. Horizontal scroll is the one thing a
   phone user cannot work around.
5. **No JavaScript for any journey.** The site's menu is a `<details>` element for this reason.
6. **The logo is one file.** `website/web/static/icon.svg`, copied into the app and rasterised
   for launcher icons; a redrawn copy diverges the moment the real one changes, silently.
