#!/usr/bin/env python3
"""Test de bout en bout : deux serveurs Jellyfin (Alice et Bob) deviennent amis et se partagent leurs bibliothèques.

Prérequis : `docker compose up -d` dans ce dossier, plugin copié dans data/*/config/plugins (voir e2e.sh).
"""
import glob
import http.client
import json
import os
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

# Les bibliothèques ne portent pas le même nom chez les deux amis : elles sont associées par type.
SERVERS = {
    "alice": {"host": "http://localhost:8101", "internal": "http://alice.localtest.me:8101", "name": "Alice", "libs": ("Films", "Séries")},
    "bob": {"host": "http://localhost:8102", "internal": "http://bob.localtest.me:8102", "name": "Bob", "libs": ("Movies", "TV Shows")},
}
FAILURES = []


def check(cond, label):
    print(("  ✔ " if cond else "  ✘ ") + label)
    if not cond:
        FAILURES.append(label)
    return cond


def request(base, method, path, body=None, token=None, raw=False, headers=None, attempts=5):
    for attempt in range(attempts):
        try:
            return _request(base, method, path, body, token, raw, headers)
        except (ConnectionError, http.client.RemoteDisconnected, urllib.error.URLError) as err:
            if isinstance(err, urllib.error.HTTPError) or attempt == attempts - 1:
                raise
            time.sleep(2)


def _request(base, method, path, body=None, token=None, raw=False, headers=None):
    auth = 'MediaBrowser Client="e2e", Device="e2e", DeviceId="e2e-%s", Version="1.0"' % base[-4:]
    if token:
        auth += ', Token="%s"' % token
    hdrs = {"Authorization": auth, "Content-Type": "application/json"}
    hdrs.update(headers or {})
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request(base + path, data=data, method=method, headers=hdrs)
    try:
        with urllib.request.urlopen(req, timeout=120) as res:
            payload = res.read()
            if raw:
                return res.status, payload, dict(res.headers)
            try:
                return res.status, (json.loads(payload) if payload else None)
            except ValueError:
                return res.status, payload.decode(errors="replace")
    except urllib.error.HTTPError as err:
        payload = err.read()
        if raw:
            return err.code, payload, dict(err.headers)
        try:
            return err.code, json.loads(payload)
        except ValueError:
            return err.code, payload.decode(errors="replace")


def wait_up(base):
    for _ in range(90):
        try:
            status, info = request(base, "GET", "/System/Info/Public", attempts=1)
            if status == 200 and isinstance(info, dict) and info.get("Id"):
                return info
        except (urllib.error.URLError, ConnectionError, OSError):
            pass
        time.sleep(1)
    raise SystemExit("serveur injoignable: " + base)


def setup_server(key):
    srv = SERVERS[key]
    base = srv["host"]
    info = wait_up(base)
    if not info.get("StartupWizardCompleted"):
        request(base, "POST", "/Startup/Configuration", {"UICulture": "fr", "MetadataCountryCode": "FR", "PreferredMetadataLanguage": "fr", "ServerName": srv["name"]})
        request(base, "GET", "/Startup/User")
        request(base, "POST", "/Startup/User", {"Name": "admin", "Password": "admin"})
        request(base, "POST", "/Startup/RemoteAccess", {"EnableRemoteAccess": True, "EnableAutomaticPortMapping": False})
        request(base, "POST", "/Startup/Complete")
    # Jellyfin 12 answers with an HTML "starting" page for a while: retry until we get a real token.
    for _ in range(60):
        status, auth = request(base, "POST", "/Users/AuthenticateByName", {"Username": "admin", "Pw": "admin"})
        if status == 200 and isinstance(auth, dict):
            break
        time.sleep(2)
    assert status == 200 and isinstance(auth, dict), str(auth)[:200]
    srv["token"] = auth["AccessToken"]
    srv["user"] = auth["User"]["Id"]

    _, folders = request(base, "GET", "/Library/VirtualFolders", token=srv["token"])
    names = {f["Name"] for f in folders}
    movies_name, shows_name = srv["libs"]
    for name, ctype, path in ((movies_name, "movies", "/media/movies"), (shows_name, "tvshows", "/media/shows")):
        if name not in names:
            q = urllib.parse.urlencode({"name": name, "collectionType": ctype, "paths": path, "refreshLibrary": "true"})
            status, res = request(base, "POST", "/Library/VirtualFolders?" + q, {"LibraryOptions": {"EnableRealtimeMonitor": False}}, token=srv["token"])
            assert status in (200, 204), res


def api(key, method, path, body=None):
    srv = SERVERS[key]
    return request(srv["host"], method, path, body, token=srv["token"])


def wait_idle(key):
    """Waits for library scans to finish."""
    time.sleep(2)
    for _ in range(180):
        _, tasks = api(key, "GET", "/ScheduledTasks?isHidden=false")
        running = [t for t in tasks if t.get("State") != "Idle"]
        _, folders = api(key, "GET", "/Library/VirtualFolders")
        refreshing = [f for f in folders if f.get("RefreshStatus") not in (None, "Idle")]
        if not running and not refreshing:
            return
        time.sleep(1)


def items(key, types):
    srv = SERVERS[key]
    q = urllib.parse.urlencode({"Recursive": "true", "IncludeItemTypes": types, "Fields": "Path,ProviderIds,MediaSources,Overview", "userId": srv["user"]})
    _, res = api(key, "GET", "/Items?" + q)
    return res["Items"]


def titles(key, types):
    return sorted(i["Name"] for i in items(key, types))


def episodes(key):
    return sorted("%s S%02dE%02d" % (i["SeriesName"], i.get("ParentIndexNumber", 0), i.get("IndexNumber", 0)) for i in items(key, "Episode"))


def main():
    print("== Préparation des serveurs")
    for key in SERVERS:
        setup_server(key)
    # Each server must have indexed its own media before pairing (scans start asynchronously).
    # Creating two libraries back to back can make Jellyfin skip the second scan: rescan until indexed.
    expected = {"alice": (3, 4), "bob": (3, 2)}
    for key in SERVERS:
        for attempt in range(60):
            wait_idle(key)
            if (len(items(key, "Movie")), len(items(key, "Episode"))) == expected[key]:
                break
            if attempt % 5 == 4:
                api(key, "POST", "/Library/Refresh")
            time.sleep(1)
        check((len(items(key, "Movie")), len(items(key, "Episode"))) == expected[key], f"{key}: médias locaux indexés")
        _, plugins = api(key, "GET", "/Plugins")
        check(any(p["Name"] == "Bloom" and p["Status"] == "Active" for p in plugins), f"{key}: plugin Bloom actif")

    print("== Réglages et identités")
    status = {}
    for key, srv in SERVERS.items():
        code, _ = api(key, "POST", "/Bloom/Admin/Settings", {"PublicUrl": srv["internal"], "ServerName": srv["name"], "SkipOwnedItems": True, "NotifyPeersOnChange": True})
        check(code == 204, f"{key}: réglages enregistrés")
        _, status[key] = api(key, "GET", "/Bloom/Admin/Status")
        print(f"     {key}: empreinte {status[key]['Identity']['Fingerprint']}")

    code, res = api("alice", "GET", "/Bloom/Peer/Info")
    check(code == 200 and res["Fingerprint"] == status["alice"]["Identity"]["Fingerprint"], "Info publique exposée")
    code, _ = request(SERVERS["alice"]["host"], "GET", "/Bloom/Admin/Status")
    check(code == 401, "API d'admin refusée sans authentification")
    alice = SERVERS["alice"]
    code, res = request(alice["host"], "POST", "/Bloom/Admin/Invites/Inspect", {"Code": "nonsense"}, token=alice["token"])
    check(code == 400 and res.get("error") == "Ce n'est pas un code d'invitation Bloom.", "les messages suivent la langue du serveur (français)")
    code, res = request(alice["host"], "POST", "/Bloom/Admin/Invites/Inspect", {"Code": "nonsense"}, token=alice["token"], headers={"X-Bloom-Language": "en-US"})
    check(code == 400 and res.get("error") == "This is not a Bloom invite code.", "… ou celle de la page qui les demande (anglais)")

    print("== Invitation d'Alice → Bob (échange de clés)")
    alice_libs = [l["Id"] for l in status["alice"]["ShareableLibraries"]]
    bob_libs = [l["Id"] for l in status["bob"]["ShareableLibraries"]]
    check(len(alice_libs) == 2 and len(bob_libs) == 2, "deux bibliothèques partageables de chaque côté")
    code, invite = api("alice", "POST", "/Bloom/Admin/Invites", {"SharedLibraryIds": alice_libs, "Label": "Bob"})
    check(code == 200 and invite["Code"].startswith("bloom1:"), "Alice crée une invitation")

    code, preview = api("bob", "POST", "/Bloom/Admin/Invites/Inspect", {"Code": invite["Code"]})
    check(code == 200 and preview["Fingerprint"] == status["alice"]["Identity"]["Fingerprint"], "Bob voit l'empreinte d'Alice avant d'accepter")

    code, accepted = api("bob", "POST", "/Bloom/Admin/Invites/Accept", {"Code": invite["Code"], "SharedLibraryIds": bob_libs})
    check(code == 200 and accepted["Name"] == "Alice", f"Bob accepte → ami « {accepted.get('Name') if isinstance(accepted, dict) else accepted} »")
    code, again = api("bob", "POST", "/Bloom/Admin/Invites/Accept", {"Code": invite["Code"], "SharedLibraryIds": bob_libs})
    check(code == 400, "une invitation ne sert qu'une fois")

    _, st_alice = api("alice", "GET", "/Bloom/Admin/Status")
    _, st_bob = api("bob", "GET", "/Bloom/Admin/Status")
    check(len(st_alice["Peers"]) == 1 and st_alice["Peers"][0]["Name"] == "Bob", "Alice connaît Bob")
    check(st_alice["Peers"][0]["Fingerprint"] == status["bob"]["Identity"]["Fingerprint"], "Alice a la bonne clé de Bob")
    check(st_bob["Peers"][0]["Fingerprint"] == status["alice"]["Identity"]["Fingerprint"], "Bob a la bonne clé d'Alice")
    alice_id = st_bob["Peers"][0]["Id"]
    bob_id = st_alice["Peers"][0]["Id"]

    print("== Synchronisation dans les deux sens")
    time.sleep(3)  # laisse finir la synchro automatique lancée par l'acceptation
    code, res = api("bob", "POST", f"/Bloom/Admin/Peers/{alice_id}/Sync")
    check(code == 200, f"Bob synchronise Alice ({res})")
    code, res = api("alice", "POST", f"/Bloom/Admin/Peers/{bob_id}/Sync")
    check(code == 200, f"Alice synchronise Bob ({res})")
    for key in SERVERS:
        wait_idle(key)

    bob_movies = titles("bob", "Movie")
    alice_movies = titles("alice", "Movie")
    print("     films chez Bob   :", bob_movies)
    print("     films chez Alice :", alice_movies)
    all_movies = ["Deux Versions", "Film en Commun", "Le Film de Bob", "Le Grand Test"]
    check(bob_movies == all_movies, "Bob voit le film d'Alice, sans doublon des films communs")
    check(alice_movies == all_movies, "Alice voit le film de Bob, sans doublon des films communs")
    bob_eps = episodes("bob")
    print("     épisodes chez Bob :", bob_eps)
    all_eps = ["Série de Test S01E01", "Série de Test S01E02", "Série de Test S01E03", "Série de Test S02E01"]
    check(bob_eps == all_eps, "Bob reçoit les épisodes qui lui manquent, sans doublon")
    check(episodes("alice") == all_eps, "Alice a tous les épisodes, sans doublon")
    check(titles("bob", "Series") == ["Série de Test"], "la série d'Alice est fusionnée avec celle de Bob (une seule série)")

    _, st_bob = api("bob", "GET", "/Bloom/Admin/Status")
    imports = {i["RemoteLibraryName"]: i for i in st_bob["Peers"][0]["Imports"]}
    print("     imports chez Bob :", {k: (v["Mode"], v.get("TargetLibrary"), v["ImportedCount"], v["SkippedCount"], v.get("VersionCount")) for k, v in imports.items()})
    check((imports["Films"]["ImportedCount"], imports["Films"]["VersionCount"], imports["Films"]["SkippedCount"]) == (2, 1, 1),
          "Films d'Alice : 2 importés dont 1 autre version, 1 identique ignoré")
    check((imports["Séries"]["ImportedCount"], imports["Séries"]["VersionCount"], imports["Séries"]["SkippedCount"]) == (3, 1, 1),
          "Séries d'Alice : 3 épisodes importés dont 1 autre version, 1 identique ignoré")
    check(imports["Films"]["Mode"] == "Merge" and imports["Films"]["TargetLibrary"] == "Movies", "les « Films » d'Alice arrivent dans « Movies » chez Bob")
    check(imports["Séries"]["Mode"] == "Merge" and imports["Séries"]["TargetLibrary"] == "TV Shows", "les « Séries » d'Alice arrivent dans « TV Shows » chez Bob")
    _, st_alice = api("alice", "GET", "/Bloom/Admin/Status")
    imports = {i["RemoteLibraryName"]: i for i in st_alice["Peers"][0]["Imports"]}
    check(imports["Movies"]["Mode"] == "Merge" and imports["Movies"]["TargetLibrary"] == "Films", "les « Movies » de Bob arrivent dans « Films » chez Alice")
    _, folders = api("bob", "GET", "/Library/VirtualFolders")
    check(sorted(f["Name"] for f in folders) == ["Movies", "TV Shows"], "aucune bibliothèque en plus chez Bob")

    print("== Versions : le même film ou épisode en plusieurs qualités")
    def sources(key, name):
        movie = next(i for i in items(key, "Movie") if i["Name"] == name)
        _, info = api(key, "POST", f"/Items/{movie['Id']}/PlaybackInfo?userId={SERVERS[key]['user']}", {})
        return movie, info["MediaSources"]
    _, common = sources("bob", "Film en Commun")
    check(len(common) == 1 and common[0]["Protocol"] == "File", "même fichier chez Alice et Bob : Bob garde le sien, pas de version en plus")
    two, versions = sources("bob", "Deux Versions")
    print("     versions chez Bob :", [(v["Name"], v["Protocol"]) for v in versions])
    check(len(versions) == 2, "chez Bob, « Deux Versions » a deux versions (une seule fiche)")
    check(bool(two.get("ImageTags", {}).get("Primary")) and two.get("Overview"), "… qui garde son affiche et son résumé")
    check(versions[0]["Protocol"] == "File" and versions[0]["Path"].startswith("/media/"), "… la sienne est lue par défaut")
    check(versions[1]["Name"] == "Alice 720p" and versions[1]["Path"].startswith(SERVERS["alice"]["internal"]), "… l'autre s'appelle « Alice 720p » et vient d'Alice")
    q = urllib.parse.urlencode({"static": "true", "mediaSourceId": versions[1]["Id"], "api_key": SERVERS["bob"]["token"]})
    code, body, _ = request(SERVERS["bob"]["host"], "GET", f"/Videos/{two['Id']}/stream?{q}", raw=True)
    alice_hd = open("data/alice/media/movies/Deux Versions (2017)/Deux Versions (2017).mkv", "rb").read()
    check(code == 200 and body == alice_hd, "choisir « Alice 720p » lit bien le fichier 720p d'Alice")
    _, versions = sources("alice", "Deux Versions")
    print("     versions chez Alice :", [(v["Name"], v["Protocol"]) for v in versions])
    check([v["Protocol"] for v in versions] == ["File", "Http"] and versions[1]["Name"] == "Bob SD", "chez Alice : la sienne d'abord, puis « Bob SD »")
    for key in SERVERS:
        _, folders = api(key, "GET", "/Library/VirtualFolders")
        lib = next(f["ItemId"] for f in folders if f["CollectionType"] == "movies")
        _, latest = api(key, "GET", f"/Items/Latest?userId={SERVERS[key]['user']}&parentId={lib}&limit=20")
        mine = next(i for i in items(key, "Movie") if i["Name"] == "Deux Versions")
        check([i["Id"] for i in latest if i["Name"] == "Deux Versions"] == [mine["Id"]],
              f"{key} : « Récemment ajoutés » montre son « Deux Versions », pas la copie de l'ami")

    def episode_sources(key, season, number):
        ep = next(i for i in items(key, "Episode") if (i.get("ParentIndexNumber"), i.get("IndexNumber")) == (season, number))
        _, info = api(key, "POST", f"/Items/{ep['Id']}/PlaybackInfo?userId={SERVERS[key]['user']}", {})
        return ep, info["MediaSources"]
    _, e1 = episode_sources("bob", 1, 1)
    check(len(e1) == 1 and e1[0]["Protocol"] == "File", "S01E01 identique : Bob garde le sien")
    e3, versions = episode_sources("bob", 1, 3)
    print("     versions de S01E03 chez Bob :", [(v["Name"], v["Protocol"]) for v in versions])
    check([v["Protocol"] for v in versions] == ["File", "Http"] and versions[1]["Name"] == "Alice 720p", "S01E03 chez Bob : le sien, puis « Alice 720p »")
    _, versions = episode_sources("alice", 1, 3)
    check([v["Protocol"] for v in versions] == ["File", "Http"] and versions[1]["Name"] == "Bob SD", "S01E03 chez Alice : le sien, puis « Bob SD »")
    show = next(i for i in items("bob", "Series"))
    _, listing = api("bob", "GET", f"/Shows/{show['Id']}/Episodes?userId={SERVERS['bob']['user']}")
    names = sorted("S%02dE%02d" % (i["ParentIndexNumber"], i["IndexNumber"]) for i in listing["Items"])
    print("     épisodes de la série chez Bob :", names)
    check(names == ["S01E01", "S01E02", "S01E03", "S02E01"], "dans la série, chaque épisode n'apparaît qu'une fois")

    print("== Lecture d'un film d'Alice depuis Bob")
    movie = next(i for i in items("bob", "Movie") if i["Name"] == "Le Grand Test")
    check(bool(movie.get("ImageTags", {}).get("Primary")), "l'affiche a été récupérée")
    original_poster = open("data/alice/media/movies/Le Grand Test (2020)/poster.jpg", "rb").read()
    posters = glob.glob("data/bob/config/data/bloom/media/*/*/Le Grand Test (2020)*/poster.jpg")
    badged = open(posters[0], "rb").read() if len(posters) == 1 else b""
    check(badged[:2] == b"\xff\xd8" and badged != original_poster, "l'affiche porte le nom d'Alice (badge)")
    code, playback = api("bob", "POST", f"/Items/{movie['Id']}/PlaybackInfo?userId={SERVERS['bob']['user']}", {})
    source = playback["MediaSources"][0]
    print("     source :", source["Path"][:90] + "…")
    check(source["Path"].startswith(SERVERS["alice"]["internal"] + "/Bloom/Peer/Stream/"), "la source pointe vers le serveur d'Alice")
    kinds = sorted({s["Type"] for s in source["MediaStreams"]})
    print("     flux :", [(s["Type"], s.get("Codec"), s.get("Language")) for s in source["MediaStreams"]])
    check("Video" in kinds and "Audio" in kinds, "Jellyfin a analysé le flux distant (vidéo + audio)")
    check(any(s["Type"] == "Subtitle" and s.get("IsExternal") for s in source["MediaStreams"]), "les sous-titres externes d'Alice sont disponibles")

    # Lecture via Bob (Bob relaie le flux d'Alice), comme un client qui ne peut pas joindre Alice directement.
    q = urllib.parse.urlencode({"static": "true", "mediaSourceId": source["Id"], "api_key": SERVERS["bob"]["token"]})
    code, body, headers = request(SERVERS["bob"]["host"], "GET", f"/Videos/{movie['Id']}/stream?{q}", raw=True)
    original = open("data/alice/media/movies/Le Grand Test (2020)/Le Grand Test (2020).mkv", "rb").read()
    check(code == 200 and body == original, f"flux complet relayé par Bob identique au fichier d'Alice ({len(body)} octets)")
    code, body, headers = request(SERVERS["bob"]["host"], "GET", f"/Videos/{movie['Id']}/stream?{q}", raw=True, headers={"Range": "bytes=1000-1999"})
    check(code == 206 and body == original[1000:2000], "reprise/avance rapide (HTTP Range) fonctionne")

    # Téléchargement (hors-ligne, ou lecture par certains clients comme Infuse) : la vidéo, pas le .strm.
    code, body, headers = request(SERVERS["bob"]["host"], "GET", f"/Items/{movie['Id']}/Download", token=SERVERS["bob"]["token"], raw=True)
    check(code == 200 and body == original, f"« Télécharger » donne la vidéo d'Alice ({len(body)} octets)")
    check("Le Grand Test (2020) - Alice SD.mkv" in headers.get("Content-Disposition", ""), "… avec le bon nom de fichier")
    code, body, headers = request(SERVERS["bob"]["host"], "GET", f"/Items/{movie['Id']}/Download", token=SERVERS["bob"]["token"], raw=True, headers={"Range": "bytes=1000-1999"})
    check(code == 206 and body == original[1000:2000], "… et l'avance rapide y marche aussi")

    # Lecture directe (client → Alice), comme le fait l'app web quand elle peut joindre Alice.
    direct = source["Path"]
    code, body, _ = request(direct.split("/Bloom")[0], "GET", "/Bloom" + direct.split("/Bloom", 1)[1], raw=True)
    check(code == 200 and body == original, "lecture directe depuis Alice avec le jeton")
    tampered = direct[:-3] + ("AAA" if not direct.endswith("AAA") else "BBB")
    code, _, _ = request(tampered.split("/Bloom")[0], "GET", "/Bloom" + tampered.split("/Bloom", 1)[1], raw=True)
    check(code == 403, "jeton modifié → refusé")
    other = next(i for i in items("alice", "Movie") if i["Name"] == "Film en Commun" and "/media/" in i["Path"])
    stolen = direct.split("/Stream/")[0] + "/Stream/" + other["Id"].replace("-", "") + "?" + direct.split("?", 1)[1]
    code, _, _ = request(stolen.split("/Bloom")[0], "GET", "/Bloom" + stolen.split("/Bloom", 1)[1], raw=True)
    check(code == 403, "jeton d'un film réutilisé pour un autre → refusé")

    print("== Sécurité de l'API entre serveurs")
    lib = alice_libs[0]
    code, res = request(SERVERS["alice"]["host"], "GET", f"/Bloom/Peer/Catalog?library={lib}")
    check(code == 401, "catalogue refusé sans signature")
    code, res = request(SERVERS["alice"]["host"], "GET", f"/Bloom/Peer/Catalog?library={lib}",
                        headers={"X-Bloom-Sender": bob_id, "X-Bloom-Timestamp": str(int(time.time())), "X-Bloom-Nonce": "abc", "X-Bloom-Signature": "AAAA"})
    check(code == 401, "catalogue refusé avec une fausse signature de Bob")

    if os.environ.get("DEMO") == "1":
        print()
        print("Mode démo : Alice et Bob restent amis.")
        print("  Alice : http://localhost:8101   Bob : http://localhost:8102   (admin / admin)")
        print("  Page de la fédération : Tableau de bord → Bloom")
        sys.exit(1 if FAILURES else 0)

    print("== Retrait du partage puis fin de la fédération")
    movies_lib = next(l["Id"] for l in status["alice"]["ShareableLibraries"] if l["CollectionType"] == "movies")
    code, _ = api("alice", "POST", f"/Bloom/Admin/Peers/{bob_id}", {"SharedLibraryIds": [l for l in alice_libs if l != movies_lib]})
    check(code == 204, "Alice ne partage plus ses films avec Bob")
    api("bob", "POST", f"/Bloom/Admin/Peers/{alice_id}/Sync")
    wait_idle("bob")
    check("Le Grand Test" not in titles("bob", "Movie"), "les films d'Alice disparaissent chez Bob")
    _, versions = sources("bob", "Deux Versions")
    check(len(versions) == 1 and versions[0]["Protocol"] == "File", "… y compris sa version de « Deux Versions » (Bob garde la sienne)")
    check("Série de Test S01E02" in episodes("bob"), "les séries d'Alice restent chez Bob")

    code, _ = api("alice", "DELETE", f"/Bloom/Admin/Peers/{bob_id}")
    check(code == 204, "Alice retire Bob de ses amis")
    time.sleep(3)
    wait_idle("alice")
    wait_idle("bob")
    _, st_bob = api("bob", "GET", "/Bloom/Admin/Status")
    check(st_bob["Peers"][0]["RevokedByPeer"], "Bob est prévenu (Alice a mis fin à la fédération)")
    check(episodes("bob") == ["Série de Test S01E01", "Série de Test S01E03"], "chez Bob, il ne reste que ses propres épisodes")
    _, versions = episode_sources("bob", 1, 3)
    check(len(versions) == 1 and versions[0]["Protocol"] == "File", "… et son S01E03 n'a plus que sa version")
    check(titles("alice", "Movie") == ["Deux Versions", "Film en Commun", "Le Grand Test"], "chez Alice, le film de Bob a disparu")

    print()
    if FAILURES:
        print(f"ÉCHEC : {len(FAILURES)} vérification(s) ratée(s)")
        for f in FAILURES:
            print("  -", f)
        sys.exit(1)
    print("Tout est OK.")


if __name__ == "__main__":
    main()
