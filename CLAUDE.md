# FrontEndTV — guide pour Claude Code

FrontEndTV est un écran d'accueil (launcher) pour **Google TV / Android TV**, développé par Florian. Le propriétaire
parle **français** :
réponds en français, simplement, et demande avant toute action qui modifie sa TV.

## Arborescence

| Dossier / fichier | Rôle |
|---|---|
| `app/src/io/freemytv/` | Code Java de l'appli (identifiant Android `io.freemytv`, à ne pas changer : les mises à jour en dépendent) |
| `HomeView.java` | L'écran d'accueil natif (vue unique dessinée à la main, calques `Layer`, ressorts `Spring`, horloge d'animation lissée). Bouton TV : pilule de verre en haut, TV du logo dessinée en vectoriel (`drawTvGlyph`) et « TV », en blanc comme les boutons voisins (`drawTvButton`, clé `TOP_LIVE`), focus dessus à l'ouverture, au bouton Accueil et au retour d'une appli (`focusTv`). Heure : chiffres qui roulent à chaque minute (`drawClockText`) |
| `SettingsActivity.java` | Écran des réglages (bleu nuit, focus violet) : à gauche les 5 catégories (`CAT_ITEMS`, `drawSidebar` ; focus au départ dessus, → ou OK entre dans la page, ← ou Retour en ressort), à droite seulement les cartes de la catégorie choisie (`drawMain`, `drawItemCard`), l'état (nouvelle version, erreur) dans la carte concernée ; catégorie Wi-Fi = réseau actuel + réseaux autour (`drawWifiPage`, `Wifi.java`), aussi ouverte par l'icône Wi-Fi en haut à droite (↑ dessus, `onCorner`) ; mention TMDB en bas de la barre si une clé est mise ; la comparaison dans « Écran d'accueil » ; reçoit aussi les réglages envoyés par l'installateur |
| `Wifi.java` | Page Wi-Fi des réglages, via l'ADB de la TV (mode FrontEndTV) : `cmd wifi status` / `start-scan` / `list-scan-results`, connexion par `cmd wifi connect-network` (Android 11+, mot de passe tapé à la télécommande, jamais écrit dans le journal) ; sinon, et pour les réseaux d'entreprise/WEP, les réglages Wi-Fi de la TV |
| `Screensaver.java` | Économiseur d'écran ; « Éteindre l'écran » (`Prefs.screenOff` : jamais, tout de suite, après 5…60 min) met la TV en veille par l'ADB (`SleepTimer.sleepNow`) |
| `HomeMode.java` / `ModeJob.java` | Bascule « FrontEndTV ⇄ Original » (désactive/réactive les applis du constructeur via l'ADB de la TV) |
| `HomeApps.java` | Dock (`SHELF`), tiroir, logos extraits des bannières des applis |
| `Updater.java` | Mises à jour : cherchées toutes les 30 min (tic de l'accueil ; 5 min après un échec, GitHub limitant à 60 requêtes/h), ligne « Mise à jour » des réglages ; lit la dernière Release GitHub (`Config.UPDATE_REPO`), télécharge `FrontEndTV.apk`, l'installe via l'ADB de la TV (`Adb.install`) |
| `Recs.java` | Recommandations TMDB (clé : `Prefs.tmdbKey()`, pays : `Prefs.tmdbRegion()`) |
| `Prefs.java` | Réglages persistants (prénom, clé TMDB, pays, ville…) |
| `app/assets/i18n/*.json` | Traductions (clé = texte anglais) |
| `app/res/drawable-nodpi/` | `icon.png` (512×512) et `banner.png` (1280×720), dessinés par `tools/make_art.py` (`python3 tools/make_art.py`, Pillow ; ne pas retoucher à la main) |
| `RemoteKeys.java` / `RemoteKeyService.java` | Touches d'applis de la télécommande : codes connus (TCL, Vestel, KTC, Sony, Xiaomi, Formovie) ; ailleurs (Philips…) touches **trouvées toutes seules** : une touche inconnue suivie (≤ 2,5 s) de l'ouverture d'une appli est notée avec elle (`RemoteKeys.onWindow` → `remote-detected.txt`) et listée « Touche Netflix — Gérée par la TV » ; ou **apprises** (Apprendre une touche ; fichiers `remote-learn` / `remote-heard` entre les processus). Une touche = son code de balayage, sinon son code de touche |
| `installer/Program.cs` | Installateur Windows `FrontEndTV-Installateur.exe` : la fenêtre (thème sombre/violet, WinForms) : ouverture animée (`Splash` : logo qui grandit, puis glisse dans l'en-tête en fondu enchaîné avec la page), liste des TV trouvées à choisir (`DeviceList` : nom, marque, modèle, IP, « Prête » ou « Débogage à activer »), pays dans un menu déroulant (`Picker`, `Countries`) |
| `installer/Discovery.cs` | Installateur : trouver les TV, seulement par connexions sortantes (pas de question du pare-feu) — dernière TV et table ARP de Windows, sinon tout le réseau par groupes de 24 ; ports 5555 (débogage : prête), 6466 (télécommande Android TV : c'est une TV), 8008 (Google Cast : `ssdp/device-desc.xml` / `eureka_info` donnent nom, marque, modèle) |
| `installer/Engine.cs` | Installateur : tout le travail avec la TV, sans interface |
| `installer/Adb.cs` | Installateur : client ADB intégré (clé RSA, `shell:`, installation par `exec:cmd package install -S`) — pas d'adb.exe |
| `installer/MakeIcon.java` | `build-exe.sh` : icône `.ico` en 11 tailles (16 → 256 px) à partir de `icon.png` |
| `installer/build-exe.sh` | Fabrique l'exe (appelé par `build.sh`, ou seul sur Windows) : `out/BuildInfo.cs` (version, URL et SHA-256 de l'APK), manifeste `app.manifest` |
| `installer/check-exe.ps1` | Compilation GitHub sur Windows : lance l'exe en `-SelfTest` (fenêtre en images) et l'analyse avec Windows Defender |
| `.claude/skills/find-skills/` | Skill « find-skills » (vercel-labs/skills, MIT) : chercher et installer d'autres skills (`npx skills`) |
| `build.sh` | Compilation sans Gradle : aapt2 → javac → d8 → zipalign → apksigner → `FrontEndTV.apk`, puis l'installateur `.exe` |

## Compiler

```bash
ANDROID_HOME=<sdk> ./build.sh      # build-tools 36 conseillé (d8 34 plantait), platform android-34, JDK 17+
```
- Le 1er lancement copie `config.example.properties` en `config.properties` (clé TMDB, ville, prénom, pays…).
- `config.properties` et `freemytv.keystore` ne sont **jamais** commités (`.gitignore`). La clé de signature doit
  rester la même pour pouvoir mettre à jour l'appli installée ; mot de passe du keystore : voir `build.sh`.
- `build.sh` crée aussi `FrontEndTV-Installateur.exe` (`installer/build-exe.sh`, `SKIP_EXE=1` pour s'en passer) avec
  le `csc.exe` livré avec Windows (Git Bash ou WSL), ou `mcs` (Mono) ailleurs. Code en **C# 5** (pas de `$"..."`, `?.`,
  `=>` pour les membres) : c'est ce que comprend le csc de Windows. L'APK n'est **pas** dans l'exe (un gros contenu
  compressé caché fait réagir les antivirus) : l'exe prend `FrontEndTV.apk` à côté de lui ou télécharge celui de la
  Release de sa version, et vérifie son SHA-256 (`out/BuildInfo.cs`). Sur GitHub, l'exe est compilé sur Windows par le
  compilateur de Microsoft, lancé en `-SelfTest` et analysé par Windows Defender (`installer/check-exe.ps1`) ; une
  erreur de démarrage est montrée et notée dans `%LOCALAPPDATA%\FrontEndTV\installateur-erreur.txt`.
- **Version publique** (Releases GitHub) : compiler avec `config.properties` = `config.example.properties`
  (aucune clé, aucune ville). Vérifier que l'APK ne contient pas la clé TMDB personnelle avant de publier.
  Publier `FrontEndTV-Installateur.exe` (et `FrontEndTV.apk` pour ceux qui installent à la main).
- **Publier une mise à jour** : augmenter `versionCode` et `versionName` (`app/AndroidManifest.xml`), écrire les
  nouveautés dans `.github/release-notes.md`, puis pousser sur `main`. `.github/workflows/release.yml` voit qu'il n'y a
  pas encore de Release `v<versionName>`, compile (avec `config.example.properties` et le secret `FREEMYTV_KEYSTORE`,
  la clé de signature en base64 — déjà enregistré) et crée la Release avec `FrontEndTV.apk` + l'exe. Pas de tag à
  pousser (le proxy de Claude Code les refuse). Les TV la trouvent dans Réglages de FrontEndTV → Mise à jour (cherché
  tout seul toutes les 30 min). Le dépôt est public : la TV lit l'API GitHub sans compte.
- Clé de signature des versions publiques : CN=FrontEndTV, O=Florian (SHA-256 `a0821902…4f52ca`), depuis la 1.2. La
  1.1 avait une autre clé : l'installateur remplace tout seul une version signée autrement.

## Installer sur une TV (Windows)

Double-clic sur `FrontEndTV-Installateur.exe` (un seul fichier, l'APK est dedans, aucun outil externe : il parle ADB
lui-même, `Adb.cs`). Après l'animation d'ouverture, la fenêtre liste les TV du réseau (`Discovery.cs`, de nouveau
toutes les 5 s tant qu'aucune TV prête n'est choisie) avec leur nom, leur marque et leur modèle ; elle choisit seule
celle de la dernière fois, ou la seule prête, et l'installation démarre alors après un compte à rebours (4 s, ou 8 s la
première fois) qui s'arrête si on touche à un réglage (TV, prénom, pays, clé TMDB — vide : la TV garde la sienne). Le programme (`Engine.cs`) :
1. crée une fois la clé ADB de ce PC (`%LOCALAPPDATA%\FrontEndTV\cle-adb.xml`) ;
2. se connecte (l'utilisateur accepte « Autoriser le débogage USB ? » sur la TV) ;
3. compare l'empreinte SHA-256 de l'APK installé : il n'envoie l'APK (`exec:cmd package install -r -S`) que s'il a
   changé ; autorise le démarrage en arrière-plan (TCL) ;
4. envoie prénom / pays / clé TMDB et lance la bascule :
   `am start -n io.freemytv/.SettingsActivity --es switch free --es tmdb_region FR --es user_name 'Florian' --es tmdb_key '…'`
5. attend la confirmation avec `cmd package resolve-activity --brief -a android.intent.action.MAIN -c android.intent.category.HOME`,
   puis appuie sur Accueil. Le second « Autoriser le débogage USB ? » (celui de l'appli) est repéré tout seul
   (`dumpsys window`, Android 11+) ; sur Android 8–10, il se déconnecte pendant ce message (sinon la réponse peut aller
   au PC) et affiche un bouton « C'est fait » (ou continue seul après 45 s).

Affichage : polices en pixels et tout à la même échelle (DPI de l'écran, réduite si la fenêtre ne tient pas en
hauteur) ; textes dessinés avec leur couleur de fond (ClearType net) ; formes arrondies remplies avec lissage (pas de
`SetClip`, qui fait des escaliers).

Essais hors de Windows : `xvfb-run mono FrontEndTV-Installateur.exe -SelfTest fenetre.png` (Mono), avec
`FRONTENDTV_SCALE=1.5` pour voir la fenêtre à 150 % (Mono ignore l'échelle de l'écran).

Lien « Remettre l'écran d'accueil d'origine » : `--es switch original`. Options en ligne de commande : `-Ip`, `-Name`,
`-Region`, `-Key`, `-Apk`, `-Original`, `-Auto` (lance tout de suite). Journal (clé TMDB masquée) :
`%LOCALAPPDATA%\FrontEndTV\installateur-journal.txt` ; mémoire (IP, prénom, pays — jamais la clé) : `installateur.ini`.

## Mesurer la fluidité

```bash
adb shell am start -n io.freemytv/.HomeActivity --ez fmtv_bench true    # parcours automatique (~50 s)
adb logcat -s FMTVBENCH                                                # images/s et temps par phase
```
La TV de test (TCL Smart TV Pro, Mali-G31) affiche à **50 Hz** : 50 images/s est le maximum.

## Règles

- Ne jamais publier une clé TMDB, une adresse ou une ville personnelle.
- Ne pas modifier les réglages système de la TV sans l'accord du propriétaire.
- Le dépôt présente FrontEndTV comme le projet de Florian, sans autre source. Seule exception, obligatoire (licence
  MIT) : les lignes de copyright de `LICENSE` et `app/assets/LICENSE.txt` restent telles quelles.
- Ne **jamais** créer de pull request : commiter et pousser directement sur la branche **`main`**, à chaque fois.
