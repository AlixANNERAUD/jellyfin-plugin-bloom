<p align="center">
  <img src="assets/logo.svg" width="140" alt="Logo de Bloom : trois méduses">
</p>

<h1 align="center">Bloom</h1>

<p align="center">
  <b>Partagez vos bibliothèques Jellyfin avec les serveurs Jellyfin de vos amis.</b><br>
  Leurs films et séries apparaissent dans vos bibliothèques, à côté des vôtres, et sont lus directement depuis leur serveur.
</p>

<p align="center">
  <a href="https://github.com/qberal/jellyfin-plugin-bloom/releases/latest"><img src="https://img.shields.io/github/v/release/qberal/jellyfin-plugin-bloom?color=00A4DC" alt="Dernière version"></a>
  <img src="https://img.shields.io/badge/Jellyfin-12.4-AA5CC3?logo=jellyfin&logoColor=white" alt="Jellyfin 12.4">
  <a href="LICENSE"><img src="https://img.shields.io/github/license/qberal/jellyfin-plugin-bloom" alt="Licence : GPL-3.0"></a>
</p>

<p align="center">
  <a href="README.md">English</a> · Français
</p>

<p align="center">
  <img src="docs/images/library.jpg" alt="La bibliothèque de films de Bob : ses films, ceux d'Alice avec un petit badge « Alice », et Tears of Steel en deux versions">
</p>

<p align="center"><sub>Les films de Bob. Ceux marqués <b>● Alice</b> viennent du serveur de son amie Alice. Bob a <i>Tears of Steel</i> en 720p et Alice en 4K : il n'apparaît qu'une fois, avec 2 versions.</sub></p>

> En anglais, un groupe de méduses s'appelle un *bloom*.

## Ce que ça fait

Chacun garde son serveur, ses fichiers et ses comptes. On s'ajoute en amis en échangeant une invitation, et on
choisit ce qu'on partage. Ensuite :

- **Leurs films et séries sont dans vos bibliothèques.** Avec affiches, résumés, acteurs et sous-titres, mélangés
  aux vôtres ou dans une bibliothèque à part, au choix. Les bibliothèques sont associées par type : leurs « Movies »
  arrivent dans vos « Films ». Vos utilisateurs n'ont rien à régler.
- **La lecture vient du serveur de votre ami.** Rien n'est copié : Bloom n'écrit que de petits fichiers `.strm`,
  `.nfo` et des images. Les lecteurs lisent directement chez votre ami, ou via votre serveur quand il faut transcoder.
- **Ça marche dans toutes les applis Jellyfin** : web, Android TV, iOS, Swiftfin, Findroid, Infuse… Un film d'ami
  est un élément Jellyfin ordinaire. *Télécharger* renvoie aussi la vraie vidéo.
- **On sait d'où vient quoi.** Un petit badge au nom de votre ami est dessiné sur ses affiches et ses images
  d'épisodes, donc il se voit dans toutes les applis. Chaque élément porte aussi un tag (`Fédération : Alice`) pour
  filtrer.
- **Pas de doublons.** Si vous avez déjà exactement le même fichier, il n'est pas importé. Si vous avez le film ou
  l'épisode dans une autre qualité, la copie de votre ami devient une **version** dans le sélecteur de version de
  Jellyfin (« Alice 4K »), et la vôtre reste celle lue par défaut. Dans une série, seuls les épisodes qui vous
  manquent sont ajoutés.
- **Tout reste à jour.** Quand un ami ajoute quelque chose, son serveur prévient le vôtre, qui se met à jour dans
  les minutes qui suivent. Une synchronisation complète passe aussi toutes les 6 heures.
- **C'est privé et révocable.** Chaque serveur a sa propre clé, les invitations se vérifient par une empreinte, et
  chaque requête entre serveurs est signée. Vous choisissez ce que voit chaque ami. Retirer un ami supprime ses
  éléments de chez vous et les vôtres de chez lui, et ses liens de lecture ne marchent plus.

<table>
  <tr>
    <td width="50%"><img src="docs/images/movie.jpg" alt="Big Buck Bunny, un film du serveur d'Alice, dans la bibliothèque de Bob"></td>
    <td width="50%"><img src="docs/images/versions.jpg" alt="Tears of Steel avec le sélecteur de version sur « Alice 4K »"></td>
  </tr>
  <tr>
    <td><sub>Un film d'ami ressemble à n'importe quel film, et se lit depuis son serveur.</sub></td>
    <td><sub>La copie de Bob est lue par défaut. Celle d'Alice, en 4K, est à un choix près.</sub></td>
  </tr>
</table>

## Installation

Bloom demande **Jellyfin 12.4** ou plus récent, sur votre serveur et sur celui de chaque ami.

### Depuis le catalogue de plugins (recommandé)

1. Dans Jellyfin, ouvrez **Tableau de bord → Extensions → Gérer les dépôts** et ajoutez un dépôt nommé `Bloom` avec
   cette URL :

   ```
   https://github.com/qberal/jellyfin-plugin-bloom/releases/latest/download/manifest.json
   ```

2. De retour sur la page **Extensions**, trouvez **Bloom** (catégorie *Général*) et installez-le.
3. Redémarrez Jellyfin. Une entrée **Bloom** apparaît dans le menu du tableau de bord.

Les mises à jour apparaissent ensuite dans le catalogue, comme pour les autres plugins.

### À la main

Téléchargez `bloom_x.y.z.0.zip` depuis la [dernière version](https://github.com/qberal/jellyfin-plugin-bloom/releases/latest),
décompressez-le dans un nouveau dossier `Bloom` du dossier des plugins de Jellyfin, puis redémarrez Jellyfin :

| Installation de Jellyfin | Dossier des plugins |
| --- | --- |
| Docker (image officielle) | `/config/plugins/` |
| Paquet Linux | `/var/lib/jellyfin/plugins/` |
| Windows | `%LOCALAPPDATA%\jellyfin\plugins\` |

## Devenir amis

1. **Ce serveur.** Indiquez l'**URL publique** de votre serveur (par exemple `https://jellyfin.exemple.fr`, voir
   [Réseau](#réseau)) et le nom que verront vos amis.
2. **Inviter un ami.** Cochez ce que vous lui partagez, créez l'invitation et envoyez-lui le code par un canal de
   confiance (Signal, WhatsApp…). Une invitation ne sert qu'une fois et expire au bout de 7 jours.
3. **Votre ami vous rejoint.** Sur son serveur, il colle le code. Bloom affiche l'**empreinte** de votre clé :
   comparez-la avec lui de vive voix ou par téléphone. Si elles sont identiques, personne ne s'est intercalé. Il coche
   ce qu'il vous partage en retour, puis accepte. Les deux serveurs se synchronisent aussitôt.
4. **Mes amis.** Pour chaque ami, vous voyez ce que vous partagez avec lui (modifiable à tout moment) et ce qu'il
   partage avec vous. Pour chacune de ses bibliothèques, choisissez où elle va : **Ajouter à « Films »** (par
   défaut : mélangée aux vôtres), **Bibliothèque séparée** (une bibliothèque au nom de la sienne est créée, par
   exemple « Movies (Alice) ») ou **Ne pas importer**.

Vous pouvez mettre un ami en pause (dans les deux sens) ou le retirer. La synchronisation se règle dans
*Tableau de bord → Tâches planifiées → Bloom*.

<p align="center">
  <img src="docs/images/friends-fr.png" width="760" alt="La carte d'un ami dans la page Bloom : ce que Bob partage avec Alice, ce qu'Alice partage avec Bob, et où ça arrive">
</p>

## Réseau

Les serveurs de vos amis, et leurs lecteurs, doivent pouvoir joindre votre serveur, et vous le leur.

- **Utilisez HTTPS.** Placez Jellyfin derrière un reverse proxy comme [Caddy](https://caddyserver.com), Nginx ou
  Traefik. Un navigateur sur une page HTTPS refuse de lire un flux HTTP.

  ```caddy
  jellyfin.exemple.fr {
      reverse_proxy localhost:8096
  }
  ```

- **Pas besoin d'exposer tout Jellyfin.** Vos amis n'appellent que `/Bloom/Peer/…` : vous pouvez publier ce
  chemin seul, sur son propre domaine, et utiliser ce domaine comme URL publique :

  ```caddy
  bloom.exemple.fr {
      @bloom path /Bloom/Peer/*
      reverse_proxy @bloom localhost:8096
      respond 404
  }
  ```

  Vérifiez depuis l'extérieur avec `https://bloom.exemple.fr/Bloom/Peer/Info` : la page doit afficher le nom et
  l'empreinte de votre serveur.
- **Derrière un CGNAT** (pas d'IPv4 publique), le plus simple est un petit VPS qui renvoie le trafic vers votre
  serveur par un VPN comme WireGuard ou Tailscale. Tailscale Funnel marche mais limite le débit. Évitez Cloudflare
  Tunnel : ses conditions restreignent la diffusion de vidéo.
- **Sur un réseau Tailscale seulement**, les serveurs se synchronisent bien, mais les lecteurs de vos amis hors du
  tailnet ne peuvent pas joindre votre serveur directement. Le client web de Jellyfin passe alors par leur propre
  serveur ; d'autres applis peuvent ne pas réussir à lire.
- **Gardez *Autoriser les connexions distantes à ce serveur*** activé dans *Tableau de bord → Réseau*.
- **Gardez l'heure juste** (NTP) : les requêtes signées avec plus de 10 minutes d'écart sont refusées.
- **Si votre serveur se met en veille** (un proxy comme nginx-hibernator ou Sablier arrête Jellyfin quand il ne
  sert pas et le relance à la requête suivante), cochez *Mon serveur se met en veille quand personne ne l'utilise*
  dans *Bloom → Ce serveur*. Les serveurs de vos amis ne le réveillent alors plus d'eux-mêmes : ils se
  synchronisent avec lui à chacun de ses démarrages, et ne le réveillent que pour lire un de vos films, en
  attendant qu'il réponde au lieu d'échouer.

**Bande passante.** Quand vous regardez un film d'un ami, c'est son débit montant qui compte : environ 5 à
15 Mbit/s pour un fichier 1080p courant, 25 à 80 Mbit/s pour de la 4K. Si votre appareil ne lit pas le format,
c'est **votre** serveur qui transcode.

## Bon à savoir

- Bloom parle français et anglais. Sa page de réglages suit la langue dans laquelle vous utilisez Jellyfin ; ce
  qu'il écrit sur le serveur (tâche planifiée, tags, erreurs de synchronisation) suit la langue du serveur.
- Films et séries uniquement pour l'instant (pas de musique ni de livres). Les rips en dossier (structure DVD ou
  Blu-ray) ne sont pas partagés.
- Ce qui est partagé n'est **jamais repartagé** : si Bob est ami avec Alice et Claire, Claire ne voit pas les
  films d'Alice.
- Si un ami est hors ligne, ses éléments restent visibles mais ne se lisent pas. Rien n'est supprimé tant que la
  synchronisation échoue.
- Chaque serveur garde son propre historique de lecture. Si un ami renomme ou ré-identifie un film, Jellyfin peut le
  traiter comme un nouveau film, et l'historique de ce film est perdu.
- Si un ami renomme son serveur, le badge et le tag suivent à la synchronisation suivante, mais le sélecteur de
  version garde le nom qu'il avait au premier import de ses éléments : renommer les fichiers ferait voir chaque
  élément comme nouveau à Jellyfin.
- **Infuse** n'affiche le badge qu'en mode *Direct* : en mode *Bibliothèque*, il utilise ses propres affiches.
- Bloom écrit les éléments de vos amis dans `<données Jellyfin>/bloom/media` (modifiable). Ne les éditez pas à la main.
  Changer ce dossier retire les éléments de vos amis puis les importe de nouveau dans le nouveau dossier : leur
  historique de lecture repart de zéro.
- Les clés et secrets sont dans `<données Jellyfin>/bloom/`, lisibles par Jellyfin seulement. Gardez ce dossier dans
  vos sauvegardes : perdre `identity.key` revient à changer d'identité, et vos amis devraient vous ré-inviter.

## Comment ça marche

```
        Serveur d'Alice                                         Serveur de Bob
 ┌──────────────────────────────┐  1. code d'invitation   ┌──────────────────────────────┐
 │ clé ECDSA  (empreinte A1…)   │ ──── (Signal, etc.) ──▶ │ clé ECDSA  (empreinte C3…)   │
 │                              │ ◀── 2. demande signée ─ │                              │
 │                              │ ── 3. réponse signée ─▶ │                              │
 │  Films  (/media/films)       │                         │  Films  (/media/films)       │
 │                              │ ◀── 4. catalogue signé ─│   + films d'Alice (.strm)    │
 │  /Bloom/Peer/Stream ◀────────┼──── 5. lecture ─────────┼── lecteur de Bob / ffmpeg    │
 └──────────────────────────────┘                         └──────────────────────────────┘
```

1. Alice crée une **invitation** : l'URL de son serveur, sa clé publique et un jeton à usage unique, valable 7 jours.
   Elle l'envoie à Bob par un canal de confiance.
2. Bob colle le code. Son serveur affiche l'**empreinte** d'Alice (à comparer de vive voix), puis envoie à Alice sa
   propre URL et sa clé publique, **signées** avec sa clé, avec le jeton.
3. Le serveur d'Alice vérifie le jeton, enregistre la clé de Bob et répond par un message **signé** avec sa clé, que
   le serveur de Bob vérifie avec la clé de l'invitation. Personne ne peut s'intercaler.
4. Chaque serveur récupère le **catalogue** de ce que l'autre lui partage (requêtes et réponses signées, horodatées,
   protégées contre le rejeu) et l'écrit dans un dossier : `Film (2010) [tmdbid-…]/Film (2010).strm`, plus `.nfo`,
   `poster.jpg` et sous-titres. Ce dossier est ajouté à votre bibliothèque, et Jellyfin l'indexe comme n'importe quel
   film.
5. Le `.strm` contient l'URL du flux chez l'ami avec un **jeton de lecture** (HMAC, valable pour ce film et pour
   votre serveur seulement, pendant 30 à 60 jours : chaque synchronisation en apporte de nouveaux). Selon
   l'appareil, le lecteur lit directement chez l'ami, ou votre serveur relaie et transcode.

Après chaque analyse de bibliothèque, Bloom regroupe les copies d'un même film ou épisode (reconnues par
identifiant TMDB, IMDb ou TVDB, puis comparées par taille de fichier) avec la fonction « versions » de Jellyfin.

Le détail du protocole est dans le [README anglais](README.md#protocol-v1).

## Développement

Il faut le SDK .NET 10, et Docker pour le test de bout en bout.

```bash
dotnet test Bloom.slnx                               # tests unitaires
./dev/e2e.sh                                         # deux vrais Jellyfin 12.4 deviennent amis : ~65 vérifications
DEMO=1 ./dev/e2e.sh                                  # idem, puis laisse tourner Alice (localhost:8101) et Bob (:8102), admin/admin
dotnet publish src/Jellyfin.Plugin.Bloom -c Release  # le plugin : Jellyfin.Plugin.Bloom.dll
```

Les versions sont construites par GitHub Actions : pousser un tag `vX.Y.Z` publie le zip et met à jour le manifeste
du catalogue. Les changements sont listés dans [CHANGELOG.md](CHANGELOG.md).

## Crédits

Les captures utilisent les films libres de la [Blender Foundation](https://studio.blender.org/films/) (CC BY), avec
les métadonnées et affiches de [TMDB](https://www.themoviedb.org). Bloom n'est pas affilié au projet Jellyfin.

Bloom est un logiciel libre, sous licence [GNU GPL v3](LICENSE).
