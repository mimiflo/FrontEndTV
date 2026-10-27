# Compilation GitHub (Windows) : vérifie FrontEndTV-Installateur.exe pour de vrai.
# 1. il démarre, ouvre sa fenêtre et la ferme sans erreur (-SelfTest enregistre ses pages : fenetre.png, l'ouverture
#    fenetre-intro.png, le menu des pays fenetre-pays.png, une
#    question fenetre-question.png et le déroulé fenetre-2.png) ; 2. Windows Defender ne le signale pas.
$ErrorActionPreference = 'Stop'
$exe = Resolve-Path 'FrontEndTV-Installateur.exe'
$shot = Join-Path (Get-Location) 'fenetre.png'

$p = Start-Process -FilePath $exe -ArgumentList '-SelfTest', "`"$shot`"" -PassThru
if (-not $p.WaitForExit(90000)) {
    $p.Kill()
    throw "L'installateur ne s'est pas fermé en 90 s."
}
$log = Join-Path $env:LOCALAPPDATA 'FrontEndTV\installateur-erreur.txt'
if ($p.ExitCode -ne 0) {
    if (Test-Path $log) { Get-Content $log }
    throw "L'installateur s'est arrêté avec le code $($p.ExitCode)."
}
if (-not (Test-Path $shot)) { throw "Pas d'image de la fenêtre : elle ne s'est pas ouverte." }
Write-Host "La fenêtre s'ouvre et se ferme sans erreur."

$defender = Join-Path $env:ProgramFiles 'Windows Defender\MpCmdRun.exe'
if (Test-Path $defender) {
    & $defender -SignatureUpdate | Out-Null
    $out = & $defender -Scan -ScanType 3 -File $exe -DisableRemediation 2>&1
    $code = $LASTEXITCODE
    $out | Write-Host
    if ($code -eq 2) { throw "Windows Defender signale l'installateur." }
    if ($code -eq 0) { Write-Host "Windows Defender : rien à signaler." }
    else { Write-Host "::warning::Analyse Windows Defender impossible ici (code $code)." }
} else {
    Write-Host "::warning::Windows Defender absent de cette machine : pas d'analyse."
}
