# FrontEndTV — guide pour Claude Code

FrontEndTV est un écran d'accueil (launcher) pour **Google TV / Android TV**, développé par Florian à partir de
Free My TV (licence MIT, crédit dans `LICENSE` et `README.md` — à garder). Le propriétaire parle **français** :
réponds en français, simplement, et demande avant toute action qui modifie sa TV.

## Arborescence

| Dossier / fichier | Rôle |
|---|---|
| `app/src/io/freemytv/` | Code Java de l'appli (identifiant Android `io.freemytv`, à ne pas changer : les mises à jour en dépendent) |
| `HomeView.java` | L'écran d'accueil natif (vue unique dessinée à la main, calques `Layer`, ressorts `Spring`, horloge d'animation lissée) |
| `SettingsActivity.java` | Écran des réglages (thème violet) ; reçoit aussi les réglages envoyés par l'installateur |
| `HomeMode.java` / `ModeJob.java` | Bascule « FrontEndTV ⇄ Original » (désactive/réactive les applis du constructeur via l'ADB de la TV) |
| `HomeApps.java` | Dock (`SHELF`), tiroir, logos extraits des bannières des applis |
| `Recs.java` | Recommandations TMDB (clé : `Prefs.tmdbKey()`, pays : `Prefs.tmdbRegion()`) |
| `Prefs.java` | Réglages persistants (prénom, clé TMDB, pays, ville…) |
| `app/assets/i18n/*.json` | Traductions (clé = texte anglais) |
| `app/res/drawable-nodpi/` | `icon.png` (192×192) et `banner.png` (640×360) |
| `installer/Program.cs` | Installateur Windows `FrontEndTV-Installateur.exe` : la fenêtre (thème sombre/violet, WinForms) |
| `installer/Engine.cs` | Installateur : tout le travail avec la TV (adb), sans interface |
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

## Installer sur une TV (Windows)

Double-clic sur `FrontEndTV-Installateur.exe` (un seul fichier, l'APK est dedans). La fenêtre propose l'adresse de la
TV (celle de la dernière fois, sinon recherche du port 5555 sur le réseau local), prénom, pays, clé TMDB (vide : la
TV garde la sienne), puis le bouton **Installer FrontEndTV**. Le programme (`Engine.cs`) :
1. trouve `adb` (ou télécharge les platform-tools officiels de Google dans `%LOCALAPPDATA%\FrontEndTV`) ;
2. se connecte (l'utilisateur accepte « Autoriser le débogage USB ? » sur la TV) ;
3. compare l'empreinte SHA-256 de l'APK installé : `adb install -r` seulement s'il a changé ; autorise le démarrage
   en arrière-plan (TCL) ;
4. envoie prénom / pays / clé TMDB et lance la bascule :
   `am start -n io.freemytv/.SettingsActivity --es switch free --es tmdb_region FR --es user_name 'Florian' --es tmdb_key '…'`
5. attend la confirmation avec `cmd package resolve-activity --brief -a android.intent.action.MAIN -c android.intent.category.HOME`,
   puis appuie sur Accueil. Le second « Autoriser le débogage USB ? » (celui de l'appli) est repéré tout seul
   (`dumpsys window`, Android 11+) ; sur Android 8–10, il se déconnecte pendant ce message (sinon la réponse peut aller
   au PC) et affiche un bouton « C'est fait » (ou continue seul après 45 s).

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
- Garder la mention de Free My TV dans `LICENSE` et `README.md` (licence MIT).
