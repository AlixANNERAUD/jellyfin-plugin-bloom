#!/usr/bin/env python3
"""Release metadata for Jellyfin.

  manifest.py meta --version 0.2.0 --target-abi 12.4.0.0 --out out/meta.json
      The meta.json shipped inside the zip: it tells Jellyfin the plugin's id and version, also when the zip
      is installed by hand.

  manifest.py repo --zip bloom_0.2.0.0.zip --version 0.2.0 ... --out manifest.json
      The repository manifest users add once (Dashboard -> Plugins -> Manage repositories). It is published as
      an asset of every release, so https://github.com/<repo>/releases/latest/download/manifest.json always
      serves the newest one, which also lists every earlier version.
"""
import argparse
import datetime
import hashlib
import json
import os
import re

PLUGIN = {
    "guid": "82c65dd9-4a92-4f73-90de-82764ea2cb05",
    "name": "Bloom",
    "description": "Share your Jellyfin libraries with your friends' Jellyfin servers: their movies and shows appear "
                   "in your libraries and play straight from their server.",
    "overview": "Library federation between friends' Jellyfin servers",
    "owner": "qberal",
    "category": "General",
}


def full_version(version):
    """0.2.0 -> 0.2.0.0 (Jellyfin versions have four parts)."""
    return version + ".0" if version.count(".") == 2 else version


def changelog(path, version):
    """Returns the section of CHANGELOG.md for a version ("## 0.1.0 ..." up to the next "## ")."""
    if not os.path.exists(path):
        return ""
    text = open(path, encoding="utf-8").read()
    match = re.search(r"^## \[?" + re.escape(version) + r"\]?[^\n]*\n(.*?)(?=^## |\Z)", text, re.S | re.M)
    return match.group(1).strip() if match else ""


def now():
    return datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def write_json(path, data):
    with open(path, "w", encoding="utf-8") as f:
        json.dump(data, f, indent=2, ensure_ascii=False)
        f.write("\n")


def meta(args):
    write_json(args.out, dict(
        PLUGIN,
        version=full_version(args.version),
        targetAbi=args.target_abi,
        changelog=changelog(args.changelog, args.version) or f"Bloom {args.version}",
        timestamp=now(),
        autoUpdate=True,
    ))


def repo(args):
    with open(args.zip, "rb") as f:
        checksum = hashlib.md5(f.read()).hexdigest()

    versions = []
    if args.previous and os.path.exists(args.previous):
        try:
            versions = json.load(open(args.previous, encoding="utf-8"))[0]["versions"]
        except (ValueError, LookupError):
            versions = []

    notes = changelog(args.changelog, args.version) or f"Bloom {args.version}"
    version = full_version(args.version)
    versions = [v for v in versions if v.get("version") != version]
    versions.insert(0, {
        "version": version,
        "changelog": notes,
        "targetAbi": args.target_abi,
        "sourceUrl": args.url,
        "checksum": checksum,
        "timestamp": now(),
    })

    write_json(args.out, [dict(PLUGIN, imageUrl=args.image_url, versions=versions)])
    if args.notes:
        with open(args.notes, "w", encoding="utf-8") as f:
            f.write(notes + "\n")
    print(f"{version}: md5 {checksum}, {len(versions)} version(s) in the manifest")


def main():
    parser = argparse.ArgumentParser()
    commands = parser.add_subparsers(dest="command", required=True)

    m = commands.add_parser("meta", help="meta.json shipped in the zip")
    r = commands.add_parser("repo", help="repository manifest")
    for p in (m, r):
        p.add_argument("--version", required=True, help="release version, e.g. 0.2.0")
        p.add_argument("--target-abi", required=True, help="oldest Jellyfin version supported, e.g. 12.4.0.0")
        p.add_argument("--changelog", default="CHANGELOG.md")
        p.add_argument("--out", required=True)
    r.add_argument("--zip", required=True, help="plugin zip")
    r.add_argument("--url", required=True, help="download URL of the zip")
    r.add_argument("--image-url", required=True)
    r.add_argument("--previous", help="manifest of the previous release, if any")
    r.add_argument("--notes", help="also write the changelog section here (release notes)")

    args = parser.parse_args()
    {"meta": meta, "repo": repo}[args.command](args)


if __name__ == "__main__":
    main()
