; Instalador do Tradutor de Reunioes (GeminiTranslateV2).
;
; O app so funciona com dois drivers de audio virtual da VB-Audio:
;   drivers\virtual1 -> VB-CABLE            (cabo virtual: leva a voz traduzida ao mic)
;   drivers\virtual2 -> Hi-Fi Cable + ASIO Bridge
;
; Como o usuario final nao sabe instalar driver, este setup instala os dois em
; SILENCIO (-i -h) durante a instalacao, verifica no registro se realmente
; entraram e, se a via silenciosa falhar, reabre o instalador original do
; fabricante para o usuario concluir. Os pacotes originais tambem ficam em
; {app}\Drivers para reinstalacao manual.
;
; Os pacotes da VB-Audio sao redistribuidos SEM MODIFICACAO. VB-CABLE e
; donationware -- origem: https://www.vb-cable.com

#define MyAppName "Tradutor de Reunioes (Gemini Live)"
#define MyAppExeName "GeminiTranslateV2.exe"
#define MyAppPublisher "Projeto Gemini Live Translate"
#define MyAppVersion "1.0"

#define VBCableDir "..\drivers\virtual1"
#define AsioDir    "..\drivers\virtual2"

[Setup]
AppId={{7F3A2C58-4D91-4E6A-9B24-C08E5A17D3F2}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\Tradutor de Reunioes
; Sem isto o Inno reusa a pasta gravada por uma instalacao anterior de mesmo
; AppId e ignora o DefaultDirName -- foi assim que o app foi parar dentro de
; "C:\Program Files\GeminiTranslateLite", sobra de uma versao antiga.
UsePreviousAppDir=no
DefaultGroupName=Tradutor de Reunioes
DisableProgramGroupPage=yes
OutputDir=output
OutputBaseFilename=TradutorReunioes-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
PrivilegesRequired=admin
UninstallDisplayIcon={app}\{#MyAppExeName}
; Os drivers de audio so passam a valer depois do boot -- o Inno oferece o
; reinicio no final quando NeedRestart() devolve True.
RestartIfNeededByRun=no

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Files]
; O publish e uma PASTA (sem PublishSingleFile): o WPF precisa das bibliotecas
; nativas em disco, senao morre na abertura com DllNotFoundException. Levamos a
; pasta inteira, menos os simbolos de debug.
Source: "..\publish-v2\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs
Source: "{#VBCableDir}\*"; DestDir: "{app}\Drivers\VB-CABLE"; Flags: ignoreversion recursesubdirs
Source: "{#AsioDir}\*";    DestDir: "{app}\Drivers\HiFiCable"; Flags: ignoreversion recursesubdirs
Source: "LEIA-ME.txt";     DestDir: "{app}\Drivers"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Reinstalar drivers de audio"; Filename: "{app}\Drivers"
Name: "{group}\Desinstalar"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na area de trabalho"; GroupDescription: "Atalhos adicionais:"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Abrir o Tradutor de Reunioes agora"; Flags: postinstall nowait skipifsilent unchecked

[Code]
const
  VBCableKey    = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\VB:VBCABLE {87459874-1236-4469}';
  AsioBridgeKey = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\VB:ASIOBridge {17359A74-1236-5467}';

var
  DriverInstalado: Boolean;   // houve instalacao nova -> pedir reinicio
  DriverFalhou: Boolean;      // sobrou algum driver sem instalar

{ O instalador de 64 bits grava em HKLM64 e o de 32 bits em HKLM32 (WOW6432Node);
  olhar as duas visoes evita depender de qual variante o fabricante usou. }
function DriverPresente(const Chave: String): Boolean;
begin
  Result := RegKeyExists(HKLM64, Chave) or RegKeyExists(HKLM32, Chave);
end;

{ Instala um driver da VB-Audio. Tenta primeiro em silencio; se a checagem no
  registro mostrar que nao entrou, reabre o instalador original visivel para o
  usuario concluir na mao -- assim uma flag silenciosa recusada nao vira uma
  instalacao quebrada e silenciosa. }
function InstalarDriver(const Titulo, Exe, Chave: String): Boolean;
var
  Code: Integer;
begin
  if DriverPresente(Chave) then
  begin
    Result := True;
    Exit;
  end;

  if not FileExists(Exe) then
  begin
    Result := False;
    Exit;
  end;

  WizardForm.StatusLabel.Caption := 'Instalando ' + Titulo + '...';
  WizardForm.Refresh();

  Exec(Exe, '-i -h', ExtractFileDir(Exe), SW_HIDE, ewWaitUntilTerminated, Code);
  if DriverPresente(Chave) then
  begin
    DriverInstalado := True;
    Result := True;
    Exit;
  end;

  WizardForm.StatusLabel.Caption := 'Concluindo a instalacao de ' + Titulo + '...';
  WizardForm.Refresh();

  Exec(Exe, '-i', ExtractFileDir(Exe), SW_SHOW, ewWaitUntilTerminated, Code);
  Result := DriverPresente(Chave);
  if Result then
    DriverInstalado := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Falhas: String;
begin
  if CurStep <> ssPostInstall then
    Exit;

  Falhas := '';
  if not InstalarDriver('VB-CABLE (cabo de audio virtual)',
                        ExpandConstant('{app}\Drivers\VB-CABLE\VBCABLE_Setup_x64.exe'),
                        VBCableKey) then
    Falhas := Falhas + #13#10 + '  - VB-CABLE';

  if not InstalarDriver('Hi-Fi Cable ASIO Bridge',
                        ExpandConstant('{app}\Drivers\HiFiCable\HiFiCableAsioBridgeSetup.exe'),
                        AsioBridgeKey) then
    Falhas := Falhas + #13#10 + '  - Hi-Fi Cable ASIO Bridge';

  if Falhas <> '' then
  begin
    DriverFalhou := True;
    MsgBox('O programa foi instalado, mas estes drivers de audio nao ficaram prontos:'
           + #13#10 + Falhas + #13#10 + #13#10 +
           'Sem eles a outra pessoa nao consegue te ouvir traduzido.' + #13#10 + #13#10 +
           'Para tentar de novo, abra o menu Iniciar -> Tradutor de Reunioes ->' + #13#10 +
           '"Reinstalar drivers de audio" e rode os instaladores que estao la.',
           mbError, MB_OK);
  end;
end;

function NeedRestart(): Boolean;
begin
  Result := DriverInstalado;
end;

procedure InitializeWizard();
begin
  DriverInstalado := False;
  DriverFalhou := False;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID <> wpFinished then
    Exit;

  if DriverFalhou then
    WizardForm.FinishedLabel.Caption :=
      'O Tradutor de Reunioes foi instalado, mas os drivers de audio ficaram' + #13#10 +
      'incompletos. Abra o menu Iniciar -> Tradutor de Reunioes ->' + #13#10 +
      '"Reinstalar drivers de audio" e rode os instaladores que estao la.'
  else if DriverInstalado then
    WizardForm.FinishedLabel.Caption :=
      'Tudo pronto! Os drivers de audio foram instalados junto com o programa.' + #13#10 + #13#10 +
      'REINICIE o computador agora -- os drivers so passam a existir depois do' + #13#10 +
      'reinicio. Depois disso, abra o Tradutor de Reunioes, cole sua API key e' + #13#10 +
      'escolha os dispositivos de audio.'
  else
    WizardForm.FinishedLabel.Caption :=
      'Tudo pronto! Os drivers de audio ja estavam instalados neste computador.' + #13#10 + #13#10 +
      'Abra o Tradutor de Reunioes, cole sua API key e escolha os dispositivos' + #13#10 +
      'de audio.';
end;
