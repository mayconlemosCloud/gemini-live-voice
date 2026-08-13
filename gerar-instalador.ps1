# Gera o instalador distribuivel: publica o app e compila o setup do Inno Setup.
#
# Uso:  .\gerar-instalador.ps1
# Saida: installer\output\TradutorReunioes-Setup.exe

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

Write-Host '[1/4] Conferindo os drivers de audio...' -ForegroundColor Cyan
# Os drivers NAO estao versionados no git (pastas grandes, de terceiros), entao
# um clone limpo compila um instalador mudo se ninguem checar isso aqui.
$exigidos = [ordered]@{
    'drivers\virtual1\VBCABLE_Setup_x64.exe'        = 'VB-CABLE'
    'drivers\virtual2\HiFiCableAsioBridgeSetup.exe' = 'Hi-Fi Cable ASIO Bridge'
}
$faltando = @($exigidos.Keys | Where-Object { -not (Test-Path $_) })
if ($faltando.Count -gt 0) {
    Write-Host ''
    Write-Warning 'Faltam instaladores de driver:'
    foreach ($f in $faltando) { Write-Host "    $f  ($($exigidos[$f]))" -ForegroundColor Yellow }
    Write-Host 'Baixe em https://vb-audio.com e coloque nas pastas acima.' -ForegroundColor Yellow
    exit 1
}
Write-Host '      VB-CABLE e Hi-Fi Cable ASIO Bridge encontrados' -ForegroundColor DarkGray

Write-Host '[2/4] Publicando o aplicativo...' -ForegroundColor Cyan
# NADA de PublishSingleFile aqui: o WPF carrega bibliotecas NATIVAS
# (PresentationNative, wpfgfx, vcruntime...) e nao consegue le-las de dentro de um
# .exe empacotado -- o app morre na abertura com DllNotFoundException no
# HwndSubclass. Publicamos em pasta e o instalador empacota a pasta inteira, que
# e o que o usuario baixa de qualquer jeito.
dotnet publish src\GeminiTranslateV2\GeminiTranslateV2.csproj `
    -c Release -r win-x64 --self-contained true `
    -o publish-v2 --nologo
if ($LASTEXITCODE -ne 0) { throw 'falha ao publicar o aplicativo.' }

# Se as nativas sumirem, o app quebra so na maquina do usuario -- entao a
# ausencia delas e erro de build, nao aviso.
$nativas = @(Get-ChildItem publish-v2\*.dll -EA SilentlyContinue)
if ($nativas.Count -eq 0) { throw 'nenhuma DLL encontrada em publish-v2 -- o publish saiu errado.' }
Write-Host "      $($nativas.Count) bibliotecas acompanham o executavel" -ForegroundColor DarkGray
if (-not (Test-Path 'publish-v2\GeminiTranslateV2.exe')) {
    throw 'publish-v2\GeminiTranslateV2.exe nao foi gerado -- confira o publish.'
}
Write-Host '[3/4] Procurando o Inno Setup...' -ForegroundColor Cyan
function Find-Iscc {
    # O winget instala por usuario (LOCALAPPDATA\Programs) e o instalador oficial
    # instala em Program Files. O registro cobre os dois casos; os caminhos fixos
    # abaixo sao so o plano B.
    $doRegistro = @(
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1'
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1'
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1'
    ) | ForEach-Object {
        try { (Get-ItemProperty -Path $_ -ErrorAction Stop).InstallLocation } catch { }
    } | Where-Object { $_ } | ForEach-Object { Join-Path $_ 'ISCC.exe' }

    @(
        $doRegistro
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}

$iscc = Find-Iscc
if (-not $iscc) {
    Write-Host '      nao encontrado, instalando via winget...' -ForegroundColor DarkGray
    winget install --id JRSoftware.InnoSetup --exact --silent `
        --accept-package-agreements --accept-source-agreements
    $iscc = Find-Iscc
}

if (-not $iscc) {
    Write-Host ''
    Write-Warning 'Inno Setup nao encontrado e a instalacao automatica falhou.'
    Write-Host 'Instale na mao e rode este script de novo:' -ForegroundColor Yellow
    Write-Host '    winget install JRSoftware.InnoSetup'
    Write-Host '    (ou baixe em https://jrsoftware.org/isdl.php)'
    exit 1
}
Write-Host "      $iscc" -ForegroundColor DarkGray

Write-Host '[4/4] Compilando o instalador...' -ForegroundColor Cyan
& $iscc 'installer\setup.iss'
if ($LASTEXITCODE -ne 0) { throw 'falha ao compilar o instalador.' }

$saida = 'installer\output\TradutorReunioes-Setup.exe'
$tamanho = [math]::Round((Get-Item $saida).Length / 1MB, 1)
Write-Host ''
Write-Host "Pronto: $saida  ($tamanho MB)" -ForegroundColor Green
Write-Host 'Esse unico arquivo instala o app e os dois drivers de audio.' -ForegroundColor DarkGray

$assinatura = (Get-AuthenticodeSignature $saida).Status
if ($assinatura -ne 'Valid') {
    Write-Host ''
    Write-Warning "O instalador esta $assinatura."
    Write-Host 'Quem baixar vai ver o aviso azul do SmartScreen e precisa clicar em' -ForegroundColor Yellow
    Write-Host '"Mais informacoes -> Executar assim mesmo". Um certificado de assinatura' -ForegroundColor Yellow
    Write-Host 'de codigo remove esse aviso.' -ForegroundColor Yellow
}
