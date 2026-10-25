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
| `installer/installer.ps1` | Installateur Windows automatique (lancé par `Installer-FrontEndTV.bat`) |
| `build.sh` | Compilation sans Gradle : aapt2 → javac → d8 → zipalign → apksigner → `FrontEndTV.apk` |

## Compiler

```bash
ANDROID_HOME=<sdk> ./build.sh      # build-tools 36 conseillé (d8 34 plantait), platform android-34, JDK 17+
```
- Le 1er lancement copie `config.example.properties` en `config.properties` (clé TMDB, ville, prénom, pays…).
- `config.properties` et `freemytv.keystore` ne sont **jamais** commités (`.gitignore`). La clé de signature doit
  rester la même pour pouvoir mettre à jour l'appli installée ; mot de passe du keystore : voir `build.sh`.
- **Version publique** (Releases GitHub) : compiler avec `config.properties` = `config.example.properties`
  (aucune clé, aucune ville). Vérifier que l'APK ne contient pas la clé TMDB personnelle avant de publier.

## Installer sur une TV (Windows)

`installer/Installer-FrontEndTV.bat` + `installer/installer.ps1` + `FrontEndTV.apk` dans le même dossier, puis
double-clic. Le script :
1. trouve `adb` (ou télécharge les platform-tools officiels de Google) ;
2. scanne le réseau local (port 5555) ou demande l'IP ;
3. se connecte (l'utilisateur accepte « Autoriser le débogage USB ? » sur la TV) ;
4. `adb install -r`, autorise le démarrage en arrière-plan (TCL) ;
5. envoie prénom / pays / clé TMDB et lance la bascule :
   `am start -n io.freemytv/.SettingsActivity --es switch free --es tmdb_region FR --es user_name 'Florian' --es tmdb_key '…'`
6. se déconnecte (Android 8–10 peut sinon donner la réponse du message suivant au PC), l'utilisateur accepte le
   second « Autoriser le débogage USB ? » (celui de l'appli elle-même), puis le script vérifie avec
   `cmd package resolve-activity --brief -a android.intent.action.MAIN -c android.intent.category.HOME`.

Options : `-Ip`, `-Name`, `-Region`, `-Key`, `-Apk`, `-Original` (remet l'accueil d'origine), `-Auto` (aucune question).
Journal : `installer/installer-log.txt`.

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
