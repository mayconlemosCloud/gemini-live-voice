# Atualiza a instalacao (C:\Program Files\GeminiTranslateLite) com o build mais recente
# de publish-lite. Precisa de admin -- o script se auto-eleva (abre UAC).

$ErrorActionPreference = 'Stop'

# Auto-elevacao: se nao estiver como admin, relanca elevado e sai.
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)) {
    Write-Host "Elevando (aceite o UAC)..."
    Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$PSCommandPath`""
    return
}

$src  = Join-Path $PSScriptRoot 'publish-lite\GeminiTranslateLite.exe'
$dest = 'C:\Program Files\GeminiTranslateLite'

if (-not (Test-Path $src)) { Write-Host "[ERRO] Nao achei $src. Rode 'dotnet publish' antes."; Read-Host 'Enter para sair'; exit 1 }

Write-Host "Fechando o app se estiver aberto..."
Get-Process GeminiTranslateLite -EA SilentlyContinue | Stop-Process -Force -EA SilentlyContinue
Start-Sleep -Milliseconds 800

Write-Host "Copiando o exe novo (162 MB, pode levar alguns segundos)..."
Copy-Item $src $dest -Force

$exe = Get-Item (Join-Path $dest 'GeminiTranslateLite.exe')
Write-Host ""
Write-Host "==> ATUALIZADO. Exe instalado agora: $($exe.LastWriteTime)" -ForegroundColor Green
Read-Host 'Enter para fechar'
