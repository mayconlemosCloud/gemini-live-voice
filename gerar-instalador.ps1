# Gera o instalador distribuivel: publica o app e compila o setup do Inno Setup.
#
# Uso:  .\gerar-instalador.ps1
# Saida: installer\output\TradutorReunioes-Setup.exe

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

Write-Host '[1/3] Publicando o aplicativo...' -ForegroundColor Cyan
dotnet publish src\GeminiTranslateV2\GeminiTranslateV2.csproj `
    -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
    -o publish-v2 --nologo
if ($LASTEXITCODE -ne 0) { throw 'falha ao publicar o aplicativo.' }

# O .NET deixa as bibliotecas NATIVAS soltas mesmo publicando em arquivo unico.
# Sem elas o app morre na abertura com DllNotFoundException, entao o instalador
# precisa levar a pasta inteira -- esta checagem existe para isso nao passar batido.
$nativas = Get-ChildItem publish-v2\*.dll -ErrorAction SilentlyContinue
Write-Host "      $($nativas.Count) bibliotecas nativas acompanham o executavel" -ForegroundColor DarkGray
if ($nativas.Count -eq 0) { Write-Warning 'nenhuma DLL nativa encontrada -- confira o publish.' }

Write-Host '[2/3] Procurando o Inno Setup...' -ForegroundColor Cyan
$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    Write-Host ''
    Write-Warning 'Inno Setup nao encontrado.'
    Write-Host 'Instale com um dos comandos abaixo e rode este script de novo:' -ForegroundColor Yellow
    Write-Host '    winget install JRSoftware.InnoSetup'
    Write-Host '    (ou baixe em https://jrsoftware.org/isdl.php)'
    exit 1
}
Write-Host "      $iscc" -ForegroundColor DarkGray

Write-Host '[3/3] Compilando o instalador...' -ForegroundColor Cyan
& $iscc 'installer\setup.iss'
if ($LASTEXITCODE -ne 0) { throw 'falha ao compilar o instalador.' }

$saida = 'installer\output\TradutorReunioes-Setup.exe'
$tamanho = [math]::Round((Get-Item $saida).Length / 1MB, 1)
Write-Host ''
Write-Host "Pronto: $saida  ($tamanho MB)" -ForegroundColor Green

$assinatura = (Get-AuthenticodeSignature $saida).Status
if ($assinatura -ne 'Valid') {
    Write-Host ''
    Write-Warning "O instalador esta $assinatura."
    Write-Host 'Quem baixar vai ver o aviso azul do SmartScreen e precisa clicar em' -ForegroundColor Yellow
    Write-Host '"Mais informacoes -> Executar assim mesmo". Um certificado de assinatura' -ForegroundColor Yellow
    Write-Host 'de codigo remove esse aviso.' -ForegroundColor Yellow
}
