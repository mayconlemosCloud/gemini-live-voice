@echo off
setlocal EnableExtensions
title Instalar - Tradutor de Reunioes (Gemini Live)

set "SRC=%~dp0"
set "APPDIR=%LOCALAPPDATA%\Programs\GeminiTranslateLite"
set "STARTMENU=%APPDATA%\Microsoft\Windows\Start Menu\Programs\Tradutor de Reunioes"

echo ============================================
echo  Instalando o Tradutor de Reunioes (Gemini)
echo ============================================
echo.

if not exist "%SRC%GeminiTranslateLite.exe" (
    echo [ERRO] GeminiTranslateLite.exe nao foi encontrado em:
    echo   %SRC%
    echo Rode este script de dentro da pasta extraida do instalador
    echo ^(nao de dentro do .zip^).
    echo.
    pause
    exit /b 1
)

echo Copiando o programa...
mkdir "%APPDIR%" >nul 2>nul
mkdir "%APPDIR%\Drivers" >nul 2>nul
rem O WPF nao empacota tudo dentro do .exe mesmo com PublishSingleFile;
rem algumas DLLs nativas (*.dll) ficam soltas ao lado dele e sao obrigatorias.
robocopy "%SRC%." "%APPDIR%" *.exe *.dll /NFL /NDL /NJH /NJS >nul

if exist "%SRC%vendor\VBCABLE" (
    echo Copiando driver VB-CABLE...
    robocopy "%SRC%vendor\VBCABLE" "%APPDIR%\Drivers\VB-CABLE" /E /NFL /NDL /NJH /NJS >nul
)
if exist "%SRC%vendor\HiFiCable" (
    echo Copiando driver Hi-Fi Cable ASIO Bridge...
    robocopy "%SRC%vendor\HiFiCable" "%APPDIR%\Drivers\HiFiCable" /E /NFL /NDL /NJH /NJS >nul
)
if exist "%SRC%LEIA-ME.txt" copy /Y "%SRC%LEIA-ME.txt" "%APPDIR%\Drivers\" >nul
if exist "%SRC%Desinstalar.bat" copy /Y "%SRC%Desinstalar.bat" "%APPDIR%\" >nul

echo Criando atalhos...
mkdir "%STARTMENU%" >nul 2>nul

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$w=New-Object -ComObject WScript.Shell;" ^
  "$desktop=[Environment]::GetFolderPath('Desktop');" ^
  "$s=$w.CreateShortcut('%STARTMENU%\Tradutor de Reunioes.lnk'); $s.TargetPath='%APPDIR%\GeminiTranslateLite.exe'; $s.WorkingDirectory='%APPDIR%'; $s.Save();" ^
  "$s=$w.CreateShortcut((Join-Path $desktop 'Tradutor de Reunioes.lnk')); $s.TargetPath='%APPDIR%\GeminiTranslateLite.exe'; $s.WorkingDirectory='%APPDIR%'; $s.Save();" ^
  "if (Test-Path '%APPDIR%\Drivers\VB-CABLE\VBCABLE_Setup_x64.exe') { $s=$w.CreateShortcut('%STARTMENU%\1 - Instalar VB-CABLE.lnk'); $s.TargetPath='%APPDIR%\Drivers\VB-CABLE\VBCABLE_Setup_x64.exe'; $s.Save() };" ^
  "if (Test-Path '%APPDIR%\Drivers\HiFiCable\HiFiCableAsioBridgeSetup.exe') { $s=$w.CreateShortcut('%STARTMENU%\2 - Instalar Hi-Fi Cable ASIO Bridge.lnk'); $s.TargetPath='%APPDIR%\Drivers\HiFiCable\HiFiCableAsioBridgeSetup.exe'; $s.Save() };" ^
  "$s=$w.CreateShortcut('%STARTMENU%\Pasta dos instaladores de audio.lnk'); $s.TargetPath='%APPDIR%\Drivers'; $s.Save();" ^
  "if (Test-Path '%APPDIR%\Desinstalar.bat') { $s=$w.CreateShortcut('%STARTMENU%\Desinstalar.lnk'); $s.TargetPath='%APPDIR%\Desinstalar.bat'; $s.Save() };"

echo.
echo ============================================
echo  Instalacao concluida!
echo ============================================
echo.
echo Falta so mais um passo, feito uma unica vez, para a outra pessoa
echo te ouvir traduzido:
echo.
echo  1^) No menu Iniciar, abra "1 - Instalar VB-CABLE" e clique em Install
echo     ^(aceite o aviso do Windows^).
echo  2^) Abra "2 - Instalar Hi-Fi Cable ASIO Bridge" e siga o assistente
echo     ^(Next, Next, Install^).
echo  3^) REINICIE o computador ^(os dois pedem isso para funcionar^).
echo  4^) Abra o Tradutor de Reunioes, cole sua API key e escolha os
echo     dispositivos de audio.
echo.
pause

if exist "%APPDIR%\Drivers" start "" "%APPDIR%\Drivers"
start "" "%APPDIR%\GeminiTranslateLite.exe"

endlocal
