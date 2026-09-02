# Isolating citron for per-title Reference research

Applies to any `*-legacy` game project using `earlier-citron` as its
test client. Two independent things to isolate: **data** (so testing one
title never corrupts another's saves/config/keys) and **network** (so a
title's traffic reaches your local mock, never the real production
servers).

## 1. Data isolation — citron's portable mode

On Linux, if a folder named `user/` exists in citron's *current working
directory* at launch, citron uses it for everything (config, saves, keys,
NAND, cache) instead of `~/.local/share/citron` + `~/.config/citron`. Pick
a dedicated directory per title, `cd` into it, then launch citron from
there.

Keep that directory **outside any git repo**. It will hold your Switch
keys/firmware, which must never enter a repo tree, even gitignored.

```sh
mkdir -p ~/.local/share/<yourtitle>-citron-profile/user
cd ~/.local/share/<yourtitle>-citron-profile
/path/to/earlier-citron/build/*/bin/citron
```

A fresh profile starts empty — copy your keys/firmware in yourself, same
as any new citron install.

## 2. Network isolation — exact-host redirect hook, not the generic one

`OPENPAK_SERVER_IP` (with `OPENPAK_ENABLE=1`) only covers Nintendo
BAAS/NEX-style hostnames. If your title uses a different backend
(Demonware, Photon, EOS, etc.), it will not be covered, and its traffic
will silently reach the real production servers instead of your mock.

Check `earlier-citron/src/core/hle/service/sockets/sfdnsres.cpp` for a
hook already matching your title's hostname — search for functions named
like `Get<Title>RedirectIp`/`Get<Title><Service>RedirectIp` (e.g.
`GetFallGuysEosRedirectIp`, `GetPhotonRedirectIp`,
`GetCtrDemonwareAuthRedirectIp`). If none exists for your hostname, add
one by mirroring an existing hook exactly:

1. A small function, exact-hostname match only, gated by its own env var
   (unset by default, so it never affects any other title's run):

   ```cpp
   static std::optional<std::string> Get<YourTitle>RedirectIp(const std::string& host) {
       if (Common::ToLower(host) != "your.exact.hostname.example.com") {
           return std::nullopt;
       }
       const char* env = std::getenv("OPENPAK_<YOURTITLE>_IP");
       if (!env || !*env) {
           return std::nullopt;
       }
       LOG_INFO(Service, "[OpenPak] Redirecting <YourTitle> host '{}' -> '{}'", host, env);
       return std::string(env);
   }
   ```

2. Wire it into the redirect chains in both
   `GetHostByNameRequestImpl` and `GetAddrInfoRequestImpl` (there are two
   near-identical resolution paths in that file — add it to both).

3. If the target port is already taken on your host (443 often is — check
   with `ss -ltnp`), add a matching port-remap block in `bsd.cpp`'s
   `ConnectImpl`, gated by its own env var, keyed off `GetLastHostForIp`
   for your exact hostname (mirror the existing Fall Guys or CTR:NF port
   blocks there).

4. Rebuild incrementally: `cd build/<your-build-dir> && ninja citron
   citron-cmd` — only the touched files and the final link need to
   rebuild, this is fast (~1 minute), not a full rebuild.

Then write a small local mock server for that endpoint. A self-signed TLS
certificate generated in memory (never written to disk — no cert/key file
to ever accidentally commit) is enough, since citron already bypasses
certificate verification for OpenPak-redirected hosts.

## 3. Wrap it in a launch script

Bake both the `cd` into the isolated profile and all the `OPENPAK_*` env
vars into one script, so a plain relaunch after a crash can't silently
drop the isolation and reach the real server again — this has happened
more than once in practice. See `ctr-legacy/scripts/launch-citron.sh`
for a working example to adapt.

## Verifying isolation is actually active

Before testing, on the exact running citron PID:

```sh
tr '\0' '\n' < /proc/<pid>/environ | grep -i legacy
readlink -f /proc/<pid>/cwd
```

Confirm the redirect env vars are present and the cwd is your isolated
profile directory. Don't assume — a bare relaunch (double-click, bare
shell command, desktop shortcut) will not carry either.
