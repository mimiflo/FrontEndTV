# FrontEndTV — guide pour Claude Code

FrontEndTV est un écran d'accueil (launcher) pour **Google TV / Android TV**, développé par Florian. Le propriétaire
parle **français** :
réponds en français, simplement, et demande avant toute action qui modifie sa TV.

## Arborescence

| Dossier / fichier | Rôle |
|---|---|
| `app/src/io/freemytv/` | Code Java de l'appli (identifiant Android `io.freemytv`, à ne pas changer : les mises à jour en dépendent) |
| `HomeView.java` | L'écran d'accueil natif (vue unique dessinée à la main, calques `Layer`, ressorts `Spring`, horloge d'animation lissée) |
| `SettingsActivity.java` | Écran des réglages (thème violet) ; reçoit aussi les réglages envoyés par l'installateur |
| `HomeMode.java` / `ModeJob.java` | Bascule « FrontEndTV ⇄ Original » (désactive/réactive les applis du constructeur via l'ADB de la TV) |
| `HomeApps.java` | Dock (`SHELF`), tiroir, logos extraits des bannières des applis |
| `Updater.java` | Mises à jour : ligne « Mise à jour » des réglages ; lit la dernière Release GitHub (`Config.UPDATE_REPO`), télécharge `FrontEndTV.apk`, l'installe via l'ADB de la TV (`Adb.install`) |
| `Recs.java` | Recommandations TMDB (clé : `Prefs.tmdbKey()`, pays : `Prefs.tmdbRegion()`) |
| `Prefs.java` | Réglages persistants (prénom, clé TMDB, pays, ville…) |
| `app/assets/i18n/*.json` | Traductions (clé = texte anglais) |
| `app/res/drawable-nodpi/` | `icon.png` (192×192) et `banner.png` (640×360) |
| `installer/Program.cs` | Installateur Windows `FrontEndTV-Installateur.exe` : la fenêtre (thème sombre/violet, WinForms) |
| `installer/Engine.cs` | Installateur : tout le travail avec la TV, sans interface |
| `installer/Adb.cs` | Installateur : client ADB intégré (clé RSA, `shell:`, installation par `exec:cmd package install -S`) — pas d'adb.exe |
| `installer/MakeIcon.java` | `build.sh` : icône `.ico` en 10 tailles (16 → 192 px) à partir de `icon.png` |
| `.claude/skills/find-skills/` | Skill « find-skills » (vercel-labs/skills, MIT) : chercher et installer d'autres skills (`npx skills`) |
| `build.sh` | Compilation sans Gradle : aapt2 → javac → d8 → zipalign → apksigner → `FrontEndTV.apk`, puis l'installateur `.exe` |

## Compiler

```bash
ANDROID_HOME=<sdk> ./build.sh      # build-tools 36 conseillé (d8 34 plantait), platform android-34, JDK 17+
```
- Le 1er lancement copie `config.example.properties` en `config.properties` (clé TMDB, ville, prénom, pays…).
- `config.properties` et `freemytv.keystore` ne sont **jamais** commités (`.gitignore`). La clé de signature doit
  rester la même pour pouvoir mettre à jour l'appli installée ; mot de passe du keystore : voir `build.sh`.
- `build.sh` crée aussi `FrontEndTV-Installateur.exe` (l'APK est dedans, en ressource) avec le `csc.exe` livré avec
  Windows (Git Bash ou WSL), ou `mcs` (Mono) ailleurs. Code en **C# 5** (pas de `$"..."`, `?.`, `=>` pour les membres) :
  c'est ce que comprend le csc de Windows.
- **Version publique** (Releases GitHub) : compiler avec `config.properties` = `config.example.properties`
  (aucune clé, aucune ville). Vérifier que l'APK ne contient pas la clé TMDB personnelle avant de publier.
  Publier `FrontEndTV-Installateur.exe` (et `FrontEndTV.apk` pour ceux qui installent à la main).
- **Publier une mise à jour** : augmenter `versionCode` et `versionName` (`app/AndroidManifest.xml`), écrire les
  nouveautés dans `.github/release-notes.md`, puis pousser sur `main`. `.github/workflows/release.yml` voit qu'il n'y a
  pas encore de Release `v<versionName>`, compile (avec `config.example.properties` et le secret `FREEMYTV_KEYSTORE`,
  la clé de signature en base64 — déjà enregistré) et crée la Release avec `FrontEndTV.apk` + l'exe. Pas de tag à
  pousser (le proxy de Claude Code les refuse). Les TV la trouvent dans Réglages de FrontEndTV → Mise à jour (vérifié
  aussi tout seul toutes les 6 h). Le dépôt est public : la TV lit l'API GitHub sans compte.
- Clé de signature des versions publiques : CN=FrontEndTV, O=Florian (SHA-256 `a0821902…4f52ca`), depuis la 1.2. La
  1.1 avait une autre clé : l'installateur remplace tout seul une version signée autrement.

## Installer sur une TV (Windows)

Double-clic sur `FrontEndTV-Installateur.exe` (un seul fichier, l'APK est dedans, aucun outil externe : il parle ADB
lui-même, `Adb.cs`). La fenêtre cherche la TV (celle de la dernière fois, sinon le port 5555 sur le réseau local, de
nouveau toutes les 4 s tant que rien ne répond) ; dès qu'elle est trouvée, l'installation démarre seule après un compte
à rebours (4 s, ou 8 s la première fois) qui s'arrête si on touche à un champ (prénom, pays, clé TMDB — vide : la TV
garde la sienne). Le programme (`Engine.cs`) :
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
