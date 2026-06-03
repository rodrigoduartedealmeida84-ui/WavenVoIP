#define MyAppName      "WavenVoIP"
#define MyAppVersion   "1.4.0"
#define MyAppPublisher "Almeida Gas"
#define MyAppURL       "https://almeidagas.com"
#define MyAppExe       "WavenVoIP.exe"
#define SourceDir      "..\WavenVoIP\bin\Release\net8.0-windows10.0.17763.0"

[Setup]
AppId={{A3F2C1D4-7E8B-4F2A-9C1D-5B3E8A2F7C4D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\Output
OutputBaseFilename=WavenVoIP_Setup
SetupIconFile={#SourceDir}\Assets\wavenvoip.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
CloseApplications=yes
CloseApplicationsFilter=*{#MyAppExe}*
RestartApplications=no
UninstallDisplayIcon={app}\{#MyAppExe}
UninstallDisplayName={#MyAppName}
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Installer
MinVersion=10.0.17763

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar ícone na Área de Trabalho"; GroupDescription: "Ícones adicionais:"; Flags: checkedonce

[Files]
; Executável principal e DLLs raiz
Source: "{#SourceDir}\{#MyAppExe}";                        DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\WavenVoIP.dll";                      DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\WavenVoIP.deps.json";                DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\WavenVoIP.runtimeconfig.json";       DestDir: "{app}"; Flags: ignoreversion

; Dependências
Source: "{#SourceDir}\BouncyCastle.Cryptography.dll";                              DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\Concentus.dll";                                              DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\DnsClient.dll";                                              DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\Google.Apis.Auth.dll";                                       DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\Google.Apis.Core.dll";                                       DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\Google.Apis.dll";                                            DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\Google.Apis.PeopleService.v1.dll";                           DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\Microsoft.Extensions.DependencyInjection.Abstractions.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\Microsoft.Extensions.Logging.Abstractions.dll";             DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\Microsoft.Windows.SDK.NET.dll";                              DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\MySqlConnector.dll";                                         DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\NAudio.Asio.dll";                                            DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\NAudio.Core.dll";                                            DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\NAudio.dll";                                                 DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\NAudio.Midi.dll";                                            DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\NAudio.Wasapi.dll";                                          DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\NAudio.WinForms.dll";                                        DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\NAudio.WinMM.dll";                                           DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\Newtonsoft.Json.dll";                                        DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\SIPSorcery.dll";                                             DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\SIPSorceryMedia.Abstractions.dll";                           DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\SIPSorceryMedia.Windows.dll";                                DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\System.Diagnostics.DiagnosticSource.dll";                    DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\System.Management.dll";                                      DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\websocket-sharp.dll";                                        DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\WinRT.Runtime.dll";                                          DestDir: "{app}"; Flags: ignoreversion

; Assets
Source: "{#SourceDir}\Assets\wavenvoip.ico";       DestDir: "{app}\Assets"; Flags: ignoreversion
Source: "{#SourceDir}\Assets\toque_padrao.mp3";    DestDir: "{app}\Assets"; Flags: ignoreversion
Source: "{#SourceDir}\Assets\ringback_tuuu.wav";   DestDir: "{app}\Assets"; Flags: ignoreversion

; Config (não sobrescreve se já existir — preserva configuração do usuário)
Source: "{#SourceDir}\Config\google_credentials.json"; DestDir: "{app}\Config"; Flags: onlyifdoesntexist uninsneveruninstall


[Icons]
Name: "{group}\{#MyAppName}";          Filename: "{app}\{#MyAppExe}"; IconFilename: "{app}\Assets\wavenvoip.ico"
Name: "{group}\Desinstalar WavenVoIP"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}";    Filename: "{app}\{#MyAppExe}"; IconFilename: "{app}\Assets\wavenvoip.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "Iniciar {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "taskkill.exe"; Parameters: "/F /IM {#MyAppExe}"; Flags: runhidden; RunOnceId: "KillWaven"

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssInstall then
    Exec('taskkill.exe', '/F /IM {#MyAppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;
