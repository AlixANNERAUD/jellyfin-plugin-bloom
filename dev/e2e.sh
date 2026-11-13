#!/usr/bin/env bash
# Construit le plugin, démarre deux Jellyfin (Alice et Bob) et lance le test de bout en bout.
#   ./dev/e2e.sh          → test complet sur des serveurs neufs
#   KEEP=1 ./dev/e2e.sh   → laisse tourner les serveurs après le test (http://localhost:8101 et :8102, admin/admin)
#   DEMO=1 ./dev/e2e.sh   → s'arrête quand Alice et Bob sont amis et synchronisés, et laisse tourner les serveurs
set -euo pipefail
cd "$(dirname "$0")"

dotnet publish ../src/Jellyfin.Plugin.Bloom -c Release -o ../artifacts/plugin --nologo -v quiet

docker compose down -v >/dev/null 2>&1 || true
# On vide les dossiers sans les supprimer : recréer un dossier monté peut le faire apparaître
# comme un fichier dans le conteneur (cache du partage de fichiers d'OrbStack/Docker Desktop).
for s in alice bob; do
  mkdir -p "data/$s/config" "data/$s/cache"
  find "data/$s/config" "data/$s/cache" -mindepth 1 -delete
done
if [ ! -f data/.media-v3 ]; then
  docker run --rm -v "$PWD":/work -w /work --entrypoint /bin/bash -e FFMPEG=/usr/lib/jellyfin-ffmpeg/ffmpeg jellyfin/jellyfin:12.4 ./make-media.sh
fi
for s in alice bob; do
  mkdir -p "data/$s/config/plugins/Bloom_0.1.0.0"
  cp ../artifacts/plugin/Jellyfin.Plugin.Bloom.dll "data/$s/config/plugins/Bloom_0.1.0.0/"
done

docker compose up -d
set +e
python3 -u e2e.py
status=$?
if [ "${KEEP:-0}" != "1" ] && [ "${DEMO:-0}" != "1" ]; then docker compose down; fi
exit $status
