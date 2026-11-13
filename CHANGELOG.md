# Changelog

## 0.2.1

- Servers that sleep when idle are supported: a proxy such as nginx-hibernator or Sablier stops Jellyfin and
  starts it again on the next request. Tick *My server sleeps when nobody uses it* in *Bloom → This server*:
  friends no longer wake your server on their own, sync with it whenever it starts, and only wake it to play one
  of your movies.
- Playing a friend's movie while their server is starting now waits for it instead of failing, and so do
  downloads (used by Infuse to play).
- Syncs no longer fail when a friend's server is starting: Bloom tries again for up to 2 minutes when it answers
  502, 503 or 504. Jellyfin itself answers 503 while it starts.
- When Jellyfin starts, Bloom tells its friends and syncs with them.

## 0.2.0

- Bloom is now in English as well as French. The settings page follows the language you use Jellyfin in;
  the scheduled task, tags and sync errors follow the server's language.
- Errors sent by a friend's server are shown in your own language.
- Dates on the settings page follow Jellyfin's language too.

## 0.1.0

First release.

- Friends' servers are added by exchanging invites (ECDSA keys, fingerprint check, signed requests).
- Their movies and shows appear in your own libraries, or in a separate one, matched by library type,
  with posters, summaries, cast and subtitles. They play straight from the friend's server.
- The friend's name is drawn as a small badge on their posters and episode images.
- What you already have is not imported again. When you have it in another quality, theirs becomes a
  version you can pick ("Alice 4K"), and yours stays the default.
- Missing episodes are added to your own series.
- Friends are told when your library changes and update within minutes; a full sync runs every 6 hours.
- "Download" in Jellyfin apps returns the friend's video.
