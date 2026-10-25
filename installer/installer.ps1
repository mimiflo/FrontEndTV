<#
  FrontEndTV — installateur automatique pour Google TV / Android TV (Windows).
  Développé par Florian.

  Double-clique sur Installer-FrontEndTV.bat (qui lance ce script), ou en ligne de commande :
    powershell -ExecutionPolicy Bypass -File installer.ps1 [-Ip 192.168.1.50] [-Name Florian] [-Region FR]
                                                          [-Key <clé TMDB>] [-Original] [-Auto]

  Ce qu'il fait :
    1. trouve adb (ou télécharge les platform-tools officiels de Google) ;
    2. cherche les TV sur le Wi-Fi (port 5555) ou demande l'adresse IP ;
    3. se connecte (accepte « Autoriser le débogage USB ? » sur la TV) ;
    4. installe FrontEndTV.apk, envoie prénom / pays / clé TMDB ;
    5. active FrontEndTV comme écran d'accueil et vérifie que c'est fait.
  -Original remet l'écran d'accueil d'origine de la TV. -Auto ne pose aucune question (valeurs par défaut).
#>
param(
    [string]$Ip,
    [string]$Apk,
    [string]$Name,
    [string]$Region,
    [string]$Key,
    [switch]$Original,
    [switch]$Auto
)

$ErrorActionPreference = 'Stop'
$PKG = 'io.freemytv'
$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch {}
try { $Host.UI.RawUI.WindowTitle = 'FrontEndTV — installation' } catch {}
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$Log = Join-Path $Here 'installer-log.txt'
"=== $(Get-Date -Format s) ===" | Out-File -FilePath $Log -Encoding utf8 -Append

function Log([string]$t) { $t | Out-File -FilePath $Log -Encoding utf8 -Append }
function Title([string]$t) { Write-Host ''; Write-Host "  $t" -ForegroundColor Magenta; Log "## $t" }
function Info([string]$t) { Write-Host "  $t"; Log $t }
function Ok([string]$t) { Write-Host "  ✔ $t" -ForegroundColor Green; Log "OK $t" }
function Warn([string]$t) { Write-Host "  ! $t" -ForegroundColor Yellow; Log "WARN $t" }
function Fail([string]$t) {
    Write-Host ''; Write-Host "  ✖ $t" -ForegroundColor Red; Log "FAIL $t"
    if (-not $Auto) { Read-Host '  Appuie sur Entrée pour fermer' | Out-Null }
    exit 1
}
function Ask([string]$q, [string]$default = '') {
    if ($Auto) { return $default }
    $suffix = if ($default) { " [$default]" } else { '' }
    $a = Read-Host "  $q$suffix"
    if ([string]::IsNullOrWhiteSpace($a)) { return $default } else { return $a.Trim() }
}

function Banner {
    Clear-Host
    Write-Host ''
    Write-Host '   ███████╗██████╗  ██████╗ ███╗   ██╗████████╗███████╗███╗   ██╗██████╗ ████████╗██╗   ██╗' -ForegroundColor DarkMagenta
    Write-Host '   ██╔════╝██╔══██╗██╔═══██╗████╗  ██║╚══██╔══╝██╔════╝████╗  ██║██╔══██╗╚══██╔══╝██║   ██║' -ForegroundColor Magenta
    Write-Host '   █████╗  ██████╔╝██║   ██║██╔██╗ ██║   ██║   █████╗  ██╔██╗ ██║██║  ██║   ██║   ██║   ██║' -ForegroundColor Magenta
    Write-Host '   ██╔══╝  ██╔══██╗██║   ██║██║╚██╗██║   ██║   ██╔══╝  ██║╚██╗██║██║  ██║   ██║   ╚██╗ ██╔╝' -ForegroundColor Magenta
    Write-Host '   ██║     ██║  ██║╚██████╔╝██║ ╚████║   ██║   ███████╗██║ ╚████║██████╔╝   ██║    ╚████╔╝ ' -ForegroundColor DarkMagenta
    Write-Host '   ╚═╝     ╚═╝  ╚═╝ ╚═════╝ ╚═╝  ╚═══╝   ╚═╝   ╚══════╝╚═╝  ╚═══╝╚═════╝    ╚═╝     ╚═══╝  ' -ForegroundColor DarkMagenta
    Write-Host '                         Écran d''accueil pour Google TV / Android TV — par Florian' -ForegroundColor Gray
    Write-Host ''
}

# --- adb -------------------------------------------------------------------------------------------------------------

function Find-Adb {
    $candidates = @(Join-Path $Here 'platform-tools\adb.exe')
    if ($env:LOCALAPPDATA) { $candidates += (Join-Path $env:LOCALAPPDATA 'Android\Sdk\platform-tools\adb.exe') }
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    $cmd = Get-Command adb.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    Info 'Téléchargement des outils Android officiels (platform-tools de Google, ~10 Mo)...'
    $zip = Join-Path $env:TEMP 'platform-tools-frontendtv.zip'
    try {
        Invoke-WebRequest -UseBasicParsing 'https://dl.google.com/android/repository/platform-tools-latest-windows.zip' -OutFile $zip
        Expand-Archive -Force $zip $Here
    } catch { Fail "Téléchargement impossible : $($_.Exception.Message)" }
    $adb = Join-Path $Here 'platform-tools\adb.exe'
    if (-not (Test-Path $adb)) { Fail 'adb introuvable après le téléchargement.' }
    return $adb
}

function Adb {
    # adb avec ses arguments ; renvoie la sortie (texte), ne s'arrête jamais sur une erreur d'adb
    # adb écrit ses messages sur stderr : sous PowerShell 5, avec ErrorActionPreference=Stop, ça arrêterait tout
    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $out = & $script:ADB @args 2>&1 | ForEach-Object { "$_" } } finally { $ErrorActionPreference = $old }
    Log ("adb " + ($args -join ' ') + " -> " + ($out -join ' | '))
    return ($out -join "`n")
}
function Shell([string]$cmd) { return (Adb -s $script:TV shell $cmd).Trim() }

# --- Trouver la TV -----------------------------------------------------------------------------------------------------

function Find-TVs {
    $found = @()
    $ips = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.IPAddress -notmatch '^(127\.|169\.254\.)' -and $_.PrefixLength -ge 16 }
    foreach ($ip in $ips) {
        $prefix = ($ip.IPAddress -split '\.')[0..2] -join '.'
        $probes = foreach ($n in 1..254) {
            $c = New-Object Net.Sockets.TcpClient
            [pscustomobject]@{ Ip = "$prefix.$n"; Client = $c; Task = $c.ConnectAsync("$prefix.$n", 5555) }
        }
        Start-Sleep -Milliseconds 1200
        foreach ($p in $probes) {
            if ($p.Task.Status -eq 'RanToCompletion' -and $p.Client.Connected) { $found += $p.Ip }
            try { $p.Client.Close() } catch {}
        }
    }
    return $found | Select-Object -Unique
}

function Connect-TV([string]$address) {
    $script:TV = "${address}:5555"
    Adb disconnect $script:TV | Out-Null
    $r = Adb connect $script:TV
    if ($r -match 'cannot|failed|refused|unable') {
        Fail "Pas de réponse de $address. Vérifie l'adresse, que la TV est allumée, sur le même Wi-Fi, et que « Débogage USB » est activé."
    }
    $warned = $false
    for ($i = 0; $i -lt 60; $i++) {
        $state = (Adb -s $script:TV get-state).Trim()
        if ($state -eq 'device') { return }
        if (-not $warned) {
            Write-Host ''
            Write-Host '  ➜ Regarde la TV : accepte « Autoriser le débogage USB ? » en cochant « Toujours autoriser ».' -ForegroundColor Cyan
            $warned = $true
        }
        Start-Sleep -Seconds 2
        if ($i % 5 -eq 4) { Adb connect $script:TV | Out-Null }
    }
    Fail 'La TV n''a pas autorisé ce PC (2 minutes). Relance et accepte le message sur la TV.'
}

function Home-Activity {
    $r = Shell 'cmd package resolve-activity --brief -a android.intent.action.MAIN -c android.intent.category.HOME'
    return ($r -split "`n" | Select-Object -Last 1).Trim()
}

# --- Réglages envoyés à l'appli ----------------------------------------------------------------------------------------

function Clean-Text([string]$t) {
    if (-not $t) { return '' }
    $n = $t.Normalize([Text.NormalizationForm]::FormD)
    $sb = New-Object Text.StringBuilder
    foreach ($ch in $n.ToCharArray()) {
        if ([Globalization.CharUnicodeInfo]::GetUnicodeCategory($ch) -ne 'NonSpacingMark') { [void]$sb.Append($ch) }
    }
    return ($sb.ToString() -replace '[^A-Za-z0-9 \-]', '').Trim()
}

function Test-TmdbKey([string]$k) {
    try {
        if ($k -match '^[0-9a-fA-F]{32}$') {
            Invoke-RestMethod -UseBasicParsing "https://api.themoviedb.org/3/configuration?api_key=$k" -TimeoutSec 15 | Out-Null
        } else {
            Invoke-RestMethod -UseBasicParsing 'https://api.themoviedb.org/3/configuration' -TimeoutSec 15 `
                -Headers @{ Authorization = "Bearer $k" } | Out-Null
        }
        return $true
    } catch { return $false }
}

function Switch-Home([string]$mode, [string]$extras) {
    Shell "am start -n $PKG/.SettingsActivity --es switch $mode $extras" | Out-Null
    # Android 8 à 10 peut donner la réponse du message suivant au PC encore connecté : on se déconnecte d'abord.
    Adb disconnect $script:TV | Out-Null
    Write-Host ''
    Write-Host '  ➜ Sur la TV, FrontEndTV demande « Autoriser le débogage USB ? » : coche « Toujours autoriser » et OK.' -ForegroundColor Cyan
    Write-Host '    (Pas de message ? C''est qu''il est déjà autorisé.)' -ForegroundColor DarkGray
    if ($Auto) { Start-Sleep -Seconds 40 } else { Read-Host '  Puis appuie sur Entrée ici' | Out-Null }
    Adb connect $script:TV | Out-Null
    Info 'Activation en cours (FrontEndTV désactive les applis publicitaires, ça peut prendre une minute)...'
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Seconds 3
        if ((Adb -s $script:TV get-state).Trim() -ne 'device') { Adb connect $script:TV | Out-Null; continue }
        $current = Home-Activity
        if ($mode -eq 'free' -and $current -like "$PKG/*") { return $true }
        if ($mode -eq 'original' -and $current -and $current -notlike "$PKG/*" -and $current -notmatch 'ResolverActivity') { return $true }
    }
    return $false
}

# --- Programme -------------------------------------------------------------------------------------------------------

Banner
Title 'Préparer la TV (une seule fois)'
Info '1. Paramètres → Système → À propos : appuie 7 fois sur « Build » (« Vous êtes développeur »).'
Info '2. Paramètres → Système → Options pour les développeurs : active « Débogage USB ».'
Info '3. La TV et ce PC doivent être sur le même Wi-Fi.'

$mode = 'free'
if ($Original) { $mode = 'original' }
elseif (-not $Auto -and -not $Ip) {
    Write-Host ''
    Write-Host '  [1] Installer ou mettre à jour FrontEndTV (recommandé)' -ForegroundColor White
    Write-Host '  [2] Remettre l''écran d''accueil d''origine de la TV' -ForegroundColor White
    $choice = Ask 'Ton choix' '1'
    if ($choice -eq '2') { $mode = 'original' }
}

Title 'Outils Android'
$script:ADB = Find-Adb
Ok "adb : $script:ADB"
Adb start-server | Out-Null

Title 'Recherche de la TV'
if (-not $Ip) {
    Info 'Je cherche les TV sur ton réseau...'
    $tvs = @(Find-TVs)
    if ($tvs.Count -eq 1) {
        Ok "TV trouvée : $($tvs[0])"
        $Ip = Ask 'Adresse IP de la TV' $tvs[0]
    } elseif ($tvs.Count -gt 1) {
        for ($i = 0; $i -lt $tvs.Count; $i++) { Info "[$($i + 1)] $($tvs[$i])" }
        $pick = Ask 'Numéro de ta TV (ou son adresse IP)' '1'
        if ($pick -match '^\d+$' -and [int]$pick -ge 1 -and [int]$pick -le $tvs.Count) { $Ip = $tvs[[int]$pick - 1] } else { $Ip = $pick }
    } else {
        Warn 'Aucune TV trouvée automatiquement (Débogage USB activé ?).'
        $Ip = Ask 'Adresse IP de la TV (Paramètres → Réseau et Internet), ex. 192.168.1.50'
    }
}
if (-not $Ip) { Fail 'Aucune adresse IP.' }
$Ip = ($Ip -replace ':5555$', '').Trim()

Title 'Connexion'
Connect-TV $Ip
$model = Shell 'getprop ro.product.model'
$brand = Shell 'getprop ro.product.manufacturer'
$sdk = 0; [void][int]::TryParse((Shell 'getprop ro.build.version.sdk'), [ref]$sdk)
$release = Shell 'getprop ro.build.version.release'
Ok "Connecté : $brand $model (Android $release)"
if ($sdk -gt 0 -and $sdk -lt 26) { Fail 'FrontEndTV demande Android 8 ou plus récent.' }

if ($mode -eq 'original') {
    Title 'Retour à l''écran d''accueil d''origine'
    if (Switch-Home 'original' '') {
        Shell 'input keyevent KEYCODE_HOME' | Out-Null
        Ok 'L''écran d''accueil d''origine est revenu, avec toutes les applis.'
        Info 'Pour supprimer FrontEndTV : Paramètres → Applications → FrontEndTV → Désinstaller.'
    } else {
        Warn 'Pas encore confirmé. Ouvre FrontEndTV sur la TV → Écran d''accueil → Original.'
    }
    Adb disconnect $script:TV | Out-Null
    if (-not $Auto) { Read-Host '  Appuie sur Entrée pour fermer' | Out-Null }
    exit 0
}

Title 'Installation de FrontEndTV'
if (-not $Apk) {
    $Apk = Join-Path $Here 'FrontEndTV.apk'
    if (-not (Test-Path $Apk)) {
        $any = Get-ChildItem -Path $Here -Filter '*.apk' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($any) { $Apk = $any.FullName }
    }
}
if (-not (Test-Path $Apk)) { Fail 'FrontEndTV.apk introuvable : mets-le dans le même dossier que l''installateur.' }
Info "Envoi de $(Split-Path -Leaf $Apk) ($([math]::Round((Get-Item $Apk).Length / 1MB, 1)) Mo)..."
Info 'Sur certaines TV, Play Protect demande de confirmer : « Plus de détails » → « Installer quand même ».'
$r = Adb -s $script:TV install -r "$Apk"
if ($r -notmatch 'Success') {
    if ($r -match 'INSTALL_FAILED_UPDATE_INCOMPATIBLE') {
        Fail 'Une autre version de FrontEndTV (signée autrement) est déjà installée : remets l''accueil d''origine, désinstalle-la, puis relance.'
    }
    Fail "L'installation a échoué : $r"
}
Ok 'Appli installée.'
# TCL et quelques marques bloquent les applis en arrière-plan : on les autorise
Shell "appops set $PKG AUTO_START allow 2>/dev/null; appops set $PKG APP_AUTO_START allow 2>/dev/null" | Out-Null

Title 'Personnalisation (Entrée pour passer)'
if (-not $PSBoundParameters.ContainsKey('Name')) { $Name = Ask 'Ton prénom, pour le message d''accueil' }
if (-not $Region) { $Region = Ask 'Ton pays en 2 lettres (FR, BE, CH, CA...)' 'FR' }
$Region = ($Region -replace '[^A-Za-z]', '').ToUpper()
if ($Region.Length -ne 2) { $Region = 'FR' }
if (-not $PSBoundParameters.ContainsKey('Key')) {
    Info 'Clé TMDB gratuite pour les recommandations de films et séries (themoviedb.org → Paramètres → API).'
    Info 'Sans clé, l''accueil marche quand même, sans les recommandations.'
    $Key = Ask 'Clé TMDB'
}
$Key = ($Key -replace '[^A-Za-z0-9\.\-_]', '')
if ($Key) {
    if (Test-TmdbKey $Key) { Ok 'Clé TMDB valide.' } else { Warn 'TMDB n''a pas accepté cette clé (ou pas d''Internet) : je l''envoie quand même.' }
}
$extras = "--es tmdb_region $Region"
$cleanName = Clean-Text $Name
if ($cleanName) { $extras += " --es user_name '$cleanName'" }
if ($Key) { $extras += " --es tmdb_key '$Key'" }

Title 'Activation comme écran d''accueil'
if (Switch-Home 'free' $extras) {
    Start-Sleep -Seconds 4
    Shell 'input keyevent KEYCODE_HOME' | Out-Null
    Ok 'FrontEndTV est ton écran d''accueil. Appuie sur le bouton Accueil de la télécommande.'
} else {
    Warn 'L''activation n''est pas encore confirmée. Ouvre FrontEndTV sur la TV : la ligne « Écran d''accueil » explique ce qui bloque.'
    Info 'Cas fréquent : un réseau Wi-Fi enregistré pendant la configuration de la TV. Oublie-le puis reconnecte-toi'
    Info '(Paramètres → Réseau et Internet), puis relance cet installateur.'
}
Adb disconnect $script:TV | Out-Null

Write-Host ''
Write-Host '  Pour revenir en arrière plus tard : relance l''installateur et choisis [2].' -ForegroundColor DarkGray
Write-Host "  Journal : $Log" -ForegroundColor DarkGray
if (-not $Auto) { Read-Host '  Appuie sur Entrée pour fermer' | Out-Null }
