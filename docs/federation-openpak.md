# How federation works

> Trimmed copy for this repository; the canonical document lives at
> `Openpak/docs/federation.md` and governs. Last synchronised 2026-09-15.

Someone in Australia runs a small box, registers it, and from then on rooms hosted by
Australians are served from Australia. Their friends in France join those rooms the same way
they always did. Nobody in Australia has an Australian account, an Australian friends list or
an Australian matchmaking server: there is one of each, at openpak.org, and the box in Sydney
is only where the packets go.

…

## Placing a room: Photon

```
console (Sydney) ─▶ home Name: authenticate ─▶ identity registered
                                                └─▶ nx-baas: "this identity is in AU"   (async, cached)
console (Sydney) ─▶ home Master: CreateGame "Q35V9E"
                       └─▶ no one in it yet, creator is AU, au-sydney announced photon
                       └─▶ reply: address = 203.0.113.9:22002
console (Sydney) ─▶ au-sydney Game: EnterRoom "Q35V9E"
au-sydney Game  ─▶ home :8080 POST /internal/node-room {room, actors:[1], props, identity}
                       └─▶ home's registry now lists Q35V9E with 1 actor and the host's props
                       └─▶ home publishes the room as the host's friend presence (nx-baas)
console (Lyon)  ─▶ home Master: JoinGame "Q35V9E"
                       └─▶ room has people in it, it is pinned to au-sydney
                       └─▶ reply: address = 203.0.113.9:22002      (whatever Lyon's country)
```

Three details carry the design:

- **The country is asked early.** The Name Server registers an identity, then asks nx-baas
  which country it lives in, without waiting for the answer. By the time the same player
  reaches CreateGame, seconds later, the answer is cached. No answer means home.
- **A room with people in it is wherever it is.** Only an empty room is placed by country. A
  joiner is sent to the room, never to their own region, which is the entire point.
- **A node reports the whole room on every change.** Photon's three roles share one
  in-process room registry, and Fusion joiners look the session up in the lobby before they
  join. So a node's Game role posts the room's actor numbers and its string-keyed game
  properties to home's health port on every join and leave, whole room each time, and home
  writes them into the same registry the lobby reads. A lost report is corrected by the next
  one rather than compounded. The creator's identity rides on the first report so home can
  publish the presence: a node never talks to the adapter.

A node runs the Game role only: with `Federation.NodeId` set the Name and Master listeners
never open. It validates tokens with home's `Server.Secret`, which is also what authenticates
its room reports.

## The announce cycle

Once a minute the node posts what it runs:

```json
{"services":{"photon":"203.0.113.9:22002","turn":"203.0.113.9:3478","stun":"203.0.113.9:3478"}}
```

The core replaces the node's `services` and touches `last_seen_at`. A node that misses three
announces drops out of `/regions`; the matchmaking servers see that within another minute and
stop placing rooms there. Rooms already on it keep going until they end: nothing tears them
down, and nothing needs to.

A service a node does not run is left out, and home's own is used for it. A node can be
TURN-only, or Photon-only.

## Trust

The node key authenticates announces and nothing else. It cannot read an account, a friends
list or a link, and `CreateNode`, `ListAllNodes` and `SetNodeApproved` refuse any caller that
is not home.

What a node *does* hold are two shared secrets: Photon's token secret, because tokens home
minted must validate on the node, and the TURN secret, because home mints the credentials
coturn checks. With those a rogue node could mislabel rooms towards home or relay traffic it
was not sent. It could not touch anyone's account. That ceiling is why approval is a person
flipping a switch after looking, and why per-node Photon secrets are the named upgrade for
the day nodes are run by people we have not met.
