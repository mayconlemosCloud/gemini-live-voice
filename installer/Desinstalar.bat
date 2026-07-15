@echo off
setlocal EnableExtensions
title Desinstalar - Tradutor de Reunioes (Gemini Live)

set "APPDIR=%LOCALAPPDATA%\Programs\GeminiTranslateLite"
set "STARTMENU=%APPDATA%\Microsoft\Windows\Start Menu\Programs\Tradutor de Reunioes"

echo Isso vai remover o Tradutor de Reunioes deste computador.
echo Os drivers VB-CABLE / Hi-Fi Cable NAO sao removidos automaticamente
echo (eles tem desinstalador proprio no Painel de Controle, se quiser
echo  remove-los tambem).
echo.
choice /M "Continuar com a desinstalacao"
if errorlevel 2 exit /b 0

taskkill /IM GeminiTranslateLite.exe /F >nul 2>nul

powershell -NoProfile -ExecutionPolicy Bypass -Command "$p = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Tradutor de Reunioes.lnk'; Remove-Item -Path $p -Force -ErrorAction SilentlyContinue"
rd /S /Q "%STARTMENU%" >nul 2>nul

echo.
echo Desinstalado.
pause

rem Este script roda de dentro de %APPDIR%, entao apagar a pasta agora
rem cortaria a leitura do proprio .bat. Delega a remocao final para um
rem processo separado e destacado, depois de um pequeno atraso.
start "" /min cmd /c "timeout /t 2 /nobreak>nul && rd /S /Q ""%APPDIR%"""
