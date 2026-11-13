#!/usr/bin/env bash
# Génère de petits médias de test (mire + bip) avec .nfo, affiche et sous-titres.
set -euo pipefail
cd "$(dirname "$0")"
FF=${FFMPEG:-ffmpeg}

video() { # out duration color [size]
  "$FF" -loglevel error -y -f lavfi -i "testsrc2=size=${4:-640x360}:rate=25:duration=$2" -f lavfi -i "sine=frequency=440:duration=$2" \
    -vf "drawbox=color=$3@0.4:t=fill" -c:v libx264 -preset ultrafast -pix_fmt yuv420p -c:a aac -shortest "$1"
}
poster() { "$FF" -loglevel error -y -f lavfi -i "color=c=$2:size=400x600" -frames:v 1 "$1"; }

movie() { # root title year tmdb color [size]
  local dir="$1/movies/$2 ($3)"; mkdir -p "$dir"
  video "$dir/$2 ($3).mkv" 12 "$5" "${6:-640x360}"
  poster "$dir/poster.jpg" "$5"
  cat > "$dir/$2 ($3).nfo" <<NFO
<?xml version="1.0" encoding="utf-8" standalone="yes"?>
<movie><title>$2</title><year>$3</year><plot>Film de test « $2 ».</plot><genre>Test</genre><uniqueid type="tmdb" default="true">$4</uniqueid><tmdbid>$4</tmdbid></movie>
NFO
  printf '1\n00:00:01,000 --> 00:00:04,000\nBonjour depuis %s\n' "$2" > "$dir/$2 ($3).fre.srt"
}

episode() { # root show season ep color [size]
  local dir="$1/shows/$2/Season 0$3"; mkdir -p "$dir"
  video "$dir/$2 S0$3E0$4.mkv" 6 "$5" "${6:-640x360}"
  cat > "$dir/$2 S0$3E0$4.nfo" <<NFO
<?xml version="1.0" encoding="utf-8" standalone="yes"?>
<episodedetails><title>Épisode $4</title><season>$3</season><episode>$4</episode><plot>Épisode de test.</plot></episodedetails>
NFO
}

show() { # root show year tvdb color
  mkdir -p "$1/shows/$2"; poster "$1/shows/$2/poster.jpg" "$5"
  cat > "$1/shows/$2/tvshow.nfo" <<NFO
<?xml version="1.0" encoding="utf-8" standalone="yes"?>
<tvshow><title>$2</title><year>$3</year><plot>Série de test.</plot><uniqueid type="tvdb" default="true">$4</uniqueid></tvshow>
NFO
}

rm -rf data/alice/media data/bob/media
# Alice : trois films et une série de 3 épisodes.
movie data/alice/media "Le Grand Test" 2020 900001 red
movie data/alice/media "Film en Commun" 2019 900003 green
movie data/alice/media "Deux Versions" 2017 900004 orange 1280x720
show data/alice/media "Série de Test" 2021 800001 purple
episode data/alice/media "Série de Test" 1 1 purple
episode data/alice/media "Série de Test" 1 2 purple
episode data/alice/media "Série de Test" 1 3 purple 1280x720
episode data/alice/media "Série de Test" 2 1 purple
# Bob : son propre film, exactement le même fichier « Film en Commun » (jamais importé : on garde le sien),
# « Deux Versions » en plus petite qualité (celle d'Alice devient une version de la sienne), et dans la série :
# exactement le même S01E01 qu'Alice, et un S01E03 en plus petite qualité.
movie data/bob/media "Le Film de Bob" 2018 900002 blue
mkdir -p data/bob/media/movies && cp -R "data/alice/media/movies/Film en Commun (2019)" data/bob/media/movies/
movie data/bob/media "Deux Versions" 2017 900004 orange 640x360
show data/bob/media "Série de Test" 2021 800001 purple
mkdir -p "data/bob/media/shows/Série de Test/Season 01"
cp "data/alice/media/shows/Série de Test/Season 01/Série de Test S01E01".* "data/bob/media/shows/Série de Test/Season 01/"
episode data/bob/media "Série de Test" 1 3 purple 640x360
touch data/.media-v3
echo "médias générés"
