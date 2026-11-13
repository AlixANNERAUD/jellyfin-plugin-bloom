# Changelog

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
