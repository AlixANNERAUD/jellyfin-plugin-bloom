<p align="center">
  <img src="assets/logo.svg" width="140" alt="Bloom logo: three jellyfish">
</p>

<h1 align="center">Bloom</h1>

<p align="center">
  <b>Share your Jellyfin libraries with your friends' Jellyfin servers.</b><br>
  Their movies and shows appear in your libraries, next to yours, and play straight from their server.
</p>

<p align="center">
  <a href="https://github.com/qberal/jellyfin-plugin-bloom/releases/latest"><img src="https://img.shields.io/github/v/release/qberal/jellyfin-plugin-bloom?color=00A4DC" alt="Latest release"></a>
  <img src="https://img.shields.io/badge/Jellyfin-12.4-AA5CC3?logo=jellyfin&logoColor=white" alt="Jellyfin 12.4">
  <a href="LICENSE"><img src="https://img.shields.io/github/license/qberal/jellyfin-plugin-bloom" alt="License: GPL-3.0"></a>
</p>

<p align="center">
  English · <a href="README.fr.md">Français</a>
</p>

<p align="center">
  <img src="docs/images/library.jpg" alt="Bob's movie library: his own movies, Alice's movies with a small 'Alice' badge, and Tears of Steel available in two versions">
</p>

<p align="center"><sub>Bob's movies. The ones marked <b>● Alice</b> come from his friend Alice's server. Bob owns <i>Tears of Steel</i> in 720p and Alice has it in 4K, so it shows once, with 2 versions.</sub></p>

> In English, a group of jellyfish is called a *bloom*.

## What it does

Every friend keeps their own server, files and accounts. You add each other by exchanging an invite and choose
what you share. Then:

- **Their movies and shows are in your libraries.** They come with posters, summaries, cast and subtitles, and
  are mixed with yours or kept in a separate library, as you prefer. Libraries are matched by type, so their
  "Films" land in your "Movies". Your users have nothing to set up.
- **Playback comes from your friend's server.** Nothing is copied: Bloom only writes small `.strm`, `.nfo` and
  image files. Players stream directly from your friend, or through your server when it has to transcode.
- **It works in every Jellyfin app**: web, Android TV, iOS, Swiftfin, Findroid, Infuse… A friend's movie is an
  ordinary Jellyfin item. *Download* also returns the actual video.
- **You can tell what comes from whom.** A small badge with your friend's name is drawn on their posters and
  episode images, so it shows on every app. Each item is also tagged "Shared by Alice", for filtering.
- **No duplicates.** If you already have exactly the same file, it is not imported. If you have the movie or
  episode in another quality, your friend's copy becomes a **version** in Jellyfin's version picker ("Alice
  4K"), and yours stays the default. In a show, only the episodes you're missing are added.
- **It stays up to date.** When a friend adds something, their server tells yours, which updates within minutes.
  A full sync also runs every 6 hours.
- **It's private and revocable.** Each server has its own key, invites are checked with a fingerprint, and every
  request between servers is signed. You choose what each friend sees. Removing a friend removes their items
  from your server and yours from theirs, and their playback links stop working.

<table>
  <tr>
    <td width="50%"><img src="docs/images/movie.jpg" alt="Big Buck Bunny, a movie from Alice's server, in Bob's library"></td>
    <td width="50%"><img src="docs/images/versions.jpg" alt="Tears of Steel with the version picker set to 'Alice 4K'"></td>
  </tr>
  <tr>
    <td><sub>A friend's movie looks like any other movie, and plays from their server.</sub></td>
    <td><sub>Bob's own copy plays by default. Alice's 4K copy is one choice away.</sub></td>
  </tr>
</table>

## Install

Bloom needs **Jellyfin 12.4** or later, on your server and on each friend's server.

### From the plugin catalog (recommended)

1. In Jellyfin, open **Dashboard → Plugins → Manage Repositories** and add a repository named `Bloom` with
   this URL:

   ```
   https://github.com/qberal/jellyfin-plugin-bloom/releases/latest/download/manifest.json
   ```

2. Back on the **Plugins** page, find **Bloom** (category *General*) and install it.
3. Restart Jellyfin. A **Bloom** entry appears in the dashboard menu.

Updates then show up in the catalog like those of any other plugin.

### By hand

Download `bloom_x.y.z.0.zip` from the [latest release](https://github.com/qberal/jellyfin-plugin-bloom/releases/latest)
and unzip it into a new `Bloom` folder inside Jellyfin's plugin folder, then restart Jellyfin:

| Jellyfin install | Plugin folder |
| --- | --- |
| Docker (official image) | `/config/plugins/` |
| Linux package | `/var/lib/jellyfin/plugins/` |
| Windows | `%LOCALAPPDATA%\jellyfin\plugins\` |

## Become friends

1. **This server.** Enter your server's **public URL** (for example `https://jellyfin.example.com`, see
   [Networking](#networking)) and the name your friends will see.
2. **Invite a friend.** Tick what you share with them, create an invite and send them the code through a channel
   you trust (Signal, WhatsApp…). An invite works once and expires after 7 days.
3. **Your friend joins.** On their server, they paste the code. Bloom shows your key's **fingerprint**: compare it
   with them by voice or phone. If both match, nobody is in between. They tick what they share with you in return,
   then accept. Both servers sync right away.
4. **My friends.** For each friend you see what you share with them (change it at any time) and what they share
   with you. For each of their libraries, choose where it goes: **Add to "Movies"** (the default: mixed with
   yours), **Separate library** (a library named after theirs is created, like "Films (Alice)"), or **Don't
   import**.

You can pause a friend (both ways) or remove them. Sync settings are under *Dashboard → Scheduled tasks → Bloom*.

<p align="center">
  <img src="docs/images/friends.png" width="760" alt="The friend card on Bloom's settings page: what Bob shares with Alice, what Alice shares with Bob, and where it goes">
</p>

## Networking

Your friends' servers, and their players, must be able to reach your server, and you must reach theirs.

- **Use HTTPS.** Put Jellyfin behind a reverse proxy such as [Caddy](https://caddyserver.com), Nginx or
  Traefik. Browsers on an HTTPS page refuse to play an HTTP stream.

  ```caddy
  jellyfin.example.com {
      reverse_proxy localhost:8096
  }
  ```

- **You don't have to expose all of Jellyfin.** Friends only ever call `/Bloom/Peer/…`, so you can publish that
  path alone, on its own domain, and use that domain as your public URL:

  ```caddy
  bloom.example.com {
      @bloom path /Bloom/Peer/*
      reverse_proxy @bloom localhost:8096
      respond 404
  }
  ```

  Check it from outside with `https://bloom.example.com/Bloom/Peer/Info`: it should show your server's name and
  fingerprint.
- **Behind CGNAT** (no public IPv4), the simplest option is a small VPS that forwards traffic to your server over
  a VPN such as WireGuard or Tailscale. Tailscale Funnel works but is limited in bandwidth. Avoid Cloudflare
  Tunnel: its terms restrict video streaming.
- **On a Tailscale network only**, servers sync fine, but your friends' players outside the tailnet can't reach
  your server directly. Jellyfin's web client then falls back to streaming through their own server; other apps
  may fail to play.
- **Keep *Allow remote connections to this server*** enabled in *Dashboard → Networking*.
- **Keep the clock right** (NTP): signed requests more than 10 minutes off are refused.
- **If your server sleeps when idle** (a proxy such as nginx-hibernator or Sablier stops Jellyfin and starts it
  again on the next request), tick *My server sleeps when nobody uses it* in *Bloom → This server*. Your friends'
  servers then never wake it on their own: they sync with it each time it starts, and only wake it to play one of
  your movies, waiting for it to answer instead of failing.

**Bandwidth.** When you watch a friend's movie, their upload speed is what counts: roughly 5–15 Mbit/s for a
typical 1080p file, 25–80 Mbit/s for 4K. If your device can't play the format, **your** server transcodes.

## Good to know

- Bloom speaks English and French. Its settings page follows the language you use Jellyfin in; what it writes on
  the server (scheduled task, tags, sync errors) follows the server's language.
- Movies and shows only, for now (no music or books). Folder rips (DVD/Blu-ray structure) aren't shared.
- Shared items are **never re-shared**: if Bob is friends with Alice and Claire, Claire doesn't see Alice's movies.
- If a friend is offline, their items stay visible but don't play. Nothing is removed while syncing fails.
- Each server keeps its own watch history. If a friend renames or re-identifies a movie, Jellyfin may see it as
  new, and the history of that movie is lost.
- If a friend renames their server, the badge and the tag follow at the next sync, but the version picker keeps
  the name they had when their items were first imported: renaming the files would make Jellyfin see every item
  as new.
- **Infuse** shows the badge in *Direct* mode only: in *Library* mode it uses its own posters.
- Bloom writes friends' items to `<Jellyfin data>/bloom/media` (configurable). Don't edit them by hand. Changing
  that folder removes friends' items and imports them again in the new one: their watch history starts over.
- Keys and secrets are in `<Jellyfin data>/bloom/`, readable by Jellyfin only. Keep that folder in your
  backups: losing `identity.key` means a new identity, and your friends would have to invite you again.

## How it works

```
         Alice's server                                         Bob's server
 ┌──────────────────────────────┐   1. invite code        ┌──────────────────────────────┐
 │ ECDSA key  (fingerprint A1…) │ ──── (Signal, etc.) ──▶ │ ECDSA key  (fingerprint C3…) │
 │                              │ ◀── 2. signed request ─ │                              │
 │                              │ ── 3. signed answer ──▶ │                              │
 │  Movies  (/media/movies)     │                         │  Movies  (/media/movies)     │
 │                              │ ◀── 4. signed catalog ─ │   + Alice's movies (.strm)   │
 │  /Bloom/Peer/Stream ◀────────┼──── 5. playback ────────┼── Bob's player / ffmpeg      │
 └──────────────────────────────┘                         └──────────────────────────────┘
```

1. Alice creates an **invite**: her server's URL, her public key and a one-time token, valid for 7 days. She
   sends it to Bob through a channel she trusts.
2. Bob pastes the code. His server shows Alice's **fingerprint** (to compare by voice), then sends Alice his own
   URL and public key, **signed** with his key, along with the token.
3. Alice's server checks the token, saves Bob's key and answers with a message **signed** with her key, which
   Bob's server checks against the key from the invite. Nobody can get in between.
4. Each server fetches the **catalog** of what the other shares with it (signed, timestamped requests and
   answers, with replay protection) and writes it to a folder: `Movie (2010) [tmdbid-…]/Movie (2010).strm`,
   plus `.nfo`, `poster.jpg` and subtitles. That folder is added to your library, and Jellyfin indexes it like
   any other movie.
5. The `.strm` file holds the stream URL on the friend's server with a **playback token** (HMAC, valid for that
   movie and your server only, for 30 to 60 days: each sync brings fresh ones). Depending on the device, the
   player reads straight from the friend, or your server relays and transcodes.

After each library scan, Bloom groups the copies of the same movie or episode (matched by TMDB, IMDb or TVDB
id, then compared by file size) using Jellyfin's own versions feature.

### Protocol (v1)

| Endpoint | Authentication | Purpose |
| --- | --- | --- |
| `GET /Bloom/Peer/Info` | none | public identity (id, name, fingerprint) |
| `POST /Bloom/Peer/Pair` | signature + invite token | key exchange |
| `POST /Bloom/Peer/Libraries` | signature | libraries shared with the caller |
| `GET /Bloom/Peer/Catalog?library=&start=&limit=` | signature | a page of the catalog (with playback tokens) |
| `GET /Bloom/Peer/Image/{id}/{type}` | signature | poster, backdrop, logo… |
| `GET /Bloom/Peer/Subtitle/{id}/{index}` | signature | external subtitles |
| `GET /Bloom/Peer/Stream/{id}?p=&t=` | HMAC token | original file, with HTTP Range |
| `POST /Bloom/Peer/Notify` | signature | "my catalog changed", or "I just started" |
| `POST /Bloom/Peer/Wake` | none | does nothing: wakes a server that sleeps when idle, before playback |
| `POST /Bloom/Peer/Unpair` | signature | end of the friendship |

Signed requests carry `X-Bloom-Sender`, `X-Bloom-Timestamp`, `X-Bloom-Nonce` and `X-Bloom-Signature`. The ECDSA
P-256 signature covers the method, the path from `/Bloom/Peer/` on (so reverse proxy prefixes don't break it),
the query, the recipient, the sender, the timestamp, the nonce and the SHA-256 of the body. JSON answers are
signed by the recipient and bound to the request's nonce.

Bloom only follows a friend to a new address once that address answers `/Bloom/Peer/Info` with the friend's id
and key, and never follows redirects: a friend's server cannot send your server's requests elsewhere, such as
to your own network.

## Development

You need the .NET 10 SDK, and Docker for the end-to-end test.

```bash
dotnet test Bloom.slnx                               # unit tests
./dev/e2e.sh                                         # two real Jellyfin 12.4 servers become friends: ~65 checks
DEMO=1 ./dev/e2e.sh                                  # same, then leaves Alice (localhost:8101) and Bob (:8102) running, admin/admin
dotnet publish src/Jellyfin.Plugin.Bloom -c Release  # the plugin: Jellyfin.Plugin.Bloom.dll
```

The end-to-end test generates test media, sets up both servers, exchanges keys, syncs both ways and checks:
libraries with different names, no duplicates, versions, posters with their badge, subtitles, relayed and direct
playback, seeking, downloads, rejection of forged tokens and signatures, then unsharing and removing a friend.

```
src/Jellyfin.Plugin.Bloom/
  Crypto/         ECDSA identity, signatures, playback tokens, replay protection
  Services/       pairing, catalog (friend side), sync and .strm/.nfo writing (your side), libraries, versions
  Api/            PeerController (server to server, signed), AdminController (dashboard)
  Configuration/  the dashboard page
tests/            unit tests
dev/              two-server docker-compose and end-to-end test
```

Releases are built by GitHub Actions: pushing a `vX.Y.Z` tag publishes the zip and updates the catalog manifest.
The changes are listed in [CHANGELOG.md](CHANGELOG.md).

## Credits

The screenshots use the open movies of the [Blender Foundation](https://studio.blender.org/films/) (CC BY), with
metadata and posters from [TMDB](https://www.themoviedb.org). Bloom isn't affiliated with the Jellyfin project.

Bloom is free software, under the [GNU GPL v3](LICENSE).
