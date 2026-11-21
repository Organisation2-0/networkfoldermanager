; ==========================================================
;  NetworkFolderManager – Installer (Inno Setup 6)
;  Wird von build-installer.ps1 kompiliert.
;  Sprachen: Deutsch / English (Auswahl beim Start des Setups)
; ==========================================================

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\output\publish"
#endif

#define AppName "NetworkFolderManager"
#define AppExe  "NetworkFolderManager.exe"
#define AppPublisher "noobnet.eu"
#define AppURL "https://github.com/Organisation2-0"
#define AppRegKey "Software\noobnet.eu\NetworkFolderManager"

[Setup]
AppId={{6F3B8E2A-4C1D-4B7E-9A55-2E8C1F0D7A31}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}
AppCopyright=© 2026 {#AppPublisher}
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} Setup
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\output
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
SetupIconFile=setup.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
; Sprachauswahl immer anzeigen (vorausgewählt: Sprache des Systems)
ShowLanguageDialog=yes
LanguageDetectionMethod=uilanguage
; Autostart und Chat-Ordner werden bewusst pro Benutzer (HKCU) eingetragen
UsedUserAreasWarning=no

[Languages]
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
; ---------------- Deutsch ----------------
german.TaskShare=Eigenen Netzwerkordner erstellen, freigeben und automatisch überwachen
german.TaskAutostart=Mit Windows starten (im Infobereich)
german.TaskAutoUpdate=Automatisch aktualisieren (neue Versionen von GitHub installieren)
german.GroupOptions=Optionen:
german.ShareTitle=Eigener Netzwerkordner
german.ShareSubtitle=Wo soll der freigegebene Ordner liegen?
german.ShareInfo=Der Ordner wird im Netzwerk freigegeben (Übertragung verschlüsselt). Legen Sie ein Passwort fest – andere verbinden sich dann mit dem Benutzernamen %1\NFM-<Freigabename> und diesem Passwort. Ihr eigenes Windows-Passwort bleibt geheim.
german.SharePath=Ordner:
german.ShareName=Freigabename (sichtbar als \\%1\Name):
german.SharePassword=Passwort für den Ordner (mind. 6 Zeichen):
german.SharePassword2=Passwort wiederholen:
german.NoPasswordWarning=WARNUNG: Ohne Passwort ist Ihr Netzwerkordner NICHT geschützt!%n%nJeder im Netzwerk mit Zugang zu diesem PC kann dann Dateien lesen, verändern oder löschen – und auch schädliche Dateien ablegen. Das ist gefährlich, besonders in fremden oder öffentlichen Netzwerken.%n%nTrotzdem OHNE Passwort fortfahren?
german.PasswordMismatch=Die Passwörter stimmen nicht überein.
german.PasswordShort=Bitte ein Passwort mit mindestens 6 Zeichen wählen.
german.MissingShareFields=Bitte Ordner und Freigabename angeben.
german.NoQuotes=Anführungszeichen sind nicht erlaubt.
german.ChatTitle=Ordner für Chat-Dateien
german.ChatSubtitle=Wo sollen empfangene Bilder und Dateien gespeichert werden?
german.ChatInfo=Bilder und Dateien, die Ihnen andere im Chat schicken, werden in diesem Ordner gespeichert. Standard ist ein neuer Ordner in Ihren Dokumenten. Sie können ihn später unter "Einstellungen" ändern.
german.ChatFolderLabel=Ordner:
german.SetupShare=Netzwerkordner wird eingerichtet...
german.SetupFirewall=Firewall wird konfiguriert...
german.ShareFailed=Der Netzwerkordner konnte nicht eingerichtet werden. Details: %1%nSie können ihn später unter "Einstellungen" im Programm anlegen.

; ---------------- English ----------------
english.TaskShare=Create my own network folder, share it and monitor it automatically
english.TaskAutostart=Start with Windows (in the notification area)
english.TaskAutoUpdate=Update automatically (install new versions from GitHub)
english.GroupOptions=Options:
english.ShareTitle=Own network folder
english.ShareSubtitle=Where should the shared folder be located?
english.ShareInfo=The folder is shared on the network (encrypted transfer). Set a password – others then connect with the user name %1\NFM-<share name> and this password. Your own Windows password stays private.
english.SharePath=Folder:
english.ShareName=Share name (visible as \\%1\Name):
english.SharePassword=Password for the folder (at least 6 characters):
english.SharePassword2=Repeat password:
english.NoPasswordWarning=WARNING: Without a password your network folder is NOT protected!%n%nAnyone on the network with access to this PC can read, change or delete files – and drop malicious files. This is dangerous, especially on unknown or public networks.%n%nContinue WITHOUT a password anyway?
english.PasswordMismatch=The passwords do not match.
english.PasswordShort=Please choose a password with at least 6 characters.
english.MissingShareFields=Please enter a folder and a share name.
english.NoQuotes=Quotation marks are not allowed.
english.ChatTitle=Folder for chat files
english.ChatSubtitle=Where should received pictures and files be saved?
english.ChatInfo=Pictures and files that others send you in the chat are saved in this folder. By default this is a new folder in your Documents. You can change it later under "Einstellungen" (settings).
english.ChatFolderLabel=Folder:
english.SetupShare=Setting up the network folder...
english.SetupFirewall=Configuring the firewall...
english.ShareFailed=The network folder could not be set up. Details: %1%nYou can create it later under "Einstellungen" (settings) in the program.

[Tasks]
Name: "createshare"; Description: "{cm:TaskShare}"; GroupDescription: "{cm:GroupOptions}"
Name: "autostart"; Description: "{cm:TaskAutostart}"; GroupDescription: "{cm:GroupOptions}"
Name: "autoupdate"; Description: "{cm:TaskAutoUpdate}"; GroupDescription: "{cm:GroupOptions}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "{#AppName}"; ValueData: """{app}\{#AppExe}"" --minimized"; Tasks: autostart; Flags: uninsdeletevalue
; Automatische Updates (dieselbe Einstellung wie im Programm unter "Einstellungen")
Root: HKCU; Subkey: "{#AppRegKey}"; ValueType: dword; ValueName: "AutoUpdate"; ValueData: 1; Tasks: autoupdate
Root: HKCU; Subkey: "{#AppRegKey}"; ValueType: dword; ValueName: "AutoUpdate"; ValueData: 0; Tasks: not autoupdate
; Sprache des Programms = gewählte Setup-Sprache (bei Updates bleibt die bisherige Einstellung erhalten)
Root: HKCU; Subkey: "{#AppRegKey}"; ValueType: string; ValueName: "Language";     ValueData: "{code:GetAppLanguage}"; Check: not IsUpdate
; Gewählter Ordner für Chat-Dateien (die App übernimmt ihn beim nächsten Start)
Root: HKCU; Subkey: "{#AppRegKey}"; ValueType: string; ValueName: "ReceiveFolder"; \
    ValueData: "{code:GetChatFolder}"; Flags: uninsdeletekey

[Dirs]
Name: "{code:GetChatFolder}"; Flags: uninsneveruninstall

[Run]
; Firewall immer einrichten (Chat + Erkennung); Netzwerkordner nur wenn gewählt
Filename: "{app}\{#AppExe}"; Parameters: "--setup-firewall"; StatusMsg: "{cm:SetupFirewall}"; \
    Flags: runhidden waituntilterminated; Check: (not WizardIsTaskSelected('createshare')) or IsUpdate
Filename: "{app}\{#AppExe}"; Parameters: "{code:GetShareParams}"; StatusMsg: "{cm:SetupShare}"; \
    Flags: runhidden waituntilterminated; Tasks: createshare; Check: not IsUpdate; AfterInstall: CheckShareResult
; Nach einem automatischen Update das Programm wieder starten (im Infobereich)
Filename: "{app}\{#AppExe}"; Parameters: "--minimized"; Flags: nowait runasoriginaluser; Check: IsUpdate
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; \
    Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{cmd}"; Parameters: "/c taskkill /f /im {#AppExe}"; Flags: runhidden; RunOnceId: "KillApp"
Filename: "{app}\{#AppExe}"; Parameters: "--remove-share"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveShare"

[UninstallDelete]
Type: dirifempty; Name: "{app}"

[Code]
var
  SharePage: TInputQueryWizardPage;
  ChatPage: TInputDirWizardPage;

{ Update-Modus (/UPDATE=1, vom Programm gestartet): bestehender Netzwerkordner und
  sein Passwort bleiben unverändert, das Programm wird danach neu gestartet. }
function IsUpdate: Boolean;
begin
  Result := ExpandConstant('{param:UPDATE|0}') = '1';
end;

procedure InitializeWizard;
var
  Previous: String;
begin
  { Seite: eigener Netzwerkordner (nur wenn die Aufgabe gewählt ist) }
  SharePage := CreateInputQueryPage(wpSelectTasks,
    CustomMessage('ShareTitle'), CustomMessage('ShareSubtitle'),
    FmtMessage(CustomMessage('ShareInfo'), [GetComputerNameString]));
  SharePage.Add(CustomMessage('SharePath'), False);
  SharePage.Add(FmtMessage(CustomMessage('ShareName'), [GetComputerNameString]), False);
  SharePage.Add(CustomMessage('SharePassword'), True);
  SharePage.Add(CustomMessage('SharePassword2'), True);
  SharePage.Values[0] := ExpandConstant('{sd}\Netzwerkordner');
  SharePage.Values[1] := 'Netzwerkordner';

  { Seite: Ordner für Chat-Dateien (Standard: Dokumente\NetworkFolderManager) }
  ChatPage := CreateInputDirPage(SharePage.ID,
    CustomMessage('ChatTitle'), CustomMessage('ChatSubtitle'), CustomMessage('ChatInfo'),
    False, '{#AppName}');
  ChatPage.Add(CustomMessage('ChatFolderLabel'));

  { Bei einer Neuinstallation über eine bestehende den bisherigen Ordner vorschlagen }
  if not RegQueryStringValue(HKCU, '{#AppRegKey}', 'ReceiveFolder', Previous) or (Previous = '') then
    Previous := ExpandConstant('{userdocs}\{#AppName}');
  ChatPage.Values[0] := Previous;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = SharePage.ID) and not WizardIsTaskSelected('createshare');
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = SharePage.ID then
  begin
    if (Trim(SharePage.Values[0]) = '') or (Trim(SharePage.Values[1]) = '') then
    begin
      MsgBox(CustomMessage('MissingShareFields'), mbError, MB_OK);
      Result := False;
    end
    else if (Pos('"', SharePage.Values[0]) > 0) or (Pos('"', SharePage.Values[1]) > 0) then
    begin
      MsgBox(CustomMessage('NoQuotes'), mbError, MB_OK);
      Result := False;
    end
    else if SharePage.Values[2] <> SharePage.Values[3] then
    begin
      MsgBox(CustomMessage('PasswordMismatch'), mbError, MB_OK);
      Result := False;
    end
    else if SharePage.Values[2] = '' then
    begin
      { Ohne Passwort nur nach ausdrücklicher Bestätigung (Standard: Nein) }
      Result := MsgBox(CustomMessage('NoPasswordWarning'), mbCriticalError, MB_YESNO or MB_DEFBUTTON2) = IDYES;
    end
    else if Length(SharePage.Values[2]) < 6 then
    begin
      MsgBox(CustomMessage('PasswordShort'), mbError, MB_OK);
      Result := False;
    end;
  end;
end;

{ Sprachcode für das Programm: "de" oder "en" }
function GetAppLanguage(Param: String): String;
begin
  if ActiveLanguage = 'german' then
    Result := 'de'
  else
    Result := 'en';
end;

function GetChatFolder(Param: String): String;
begin
  Result := ChatPage.Values[0];
end;

function GetShareParams(Param: String): String;
var
  Path, PwFile: String;
  Lines: TArrayOfString;
begin
  Path := Trim(SharePage.Values[0]);
  { Abschließenden Backslash entfernen, sonst maskiert er das Anführungszeichen }
  while (Length(Path) > 3) and (Path[Length(Path)] = '\') do
    Delete(Path, Length(Path), 1);
  Result := '--setup-share "' + Path + '" "' + Trim(SharePage.Values[1]) + '"';

  { Passwort nicht auf der Kommandozeile übergeben, sondern über eine temporäre Datei,
    die das Programm sofort nach dem Lesen löscht (der Setup-Temp-Ordner wird am Ende ohnehin entfernt). }
  if SharePage.Values[2] <> '' then
  begin
    PwFile := ExpandConstant('{tmp}\nfm-pw.tmp');
    SetArrayLength(Lines, 1);
    Lines[0] := SharePage.Values[2];
    SaveStringsToUTF8File(PwFile, Lines, False);
    Result := Result + ' "' + PwFile + '"';
  end;
end;

procedure CheckShareResult;
var
  Log: String;
begin
  if not FileExists(ExpandConstant('{commonappdata}\NetworkFolderManager\machine.json')) then
  begin
    Log := ExpandConstant('{commonappdata}\NetworkFolderManager\setup.log');
    if not WizardSilent then
      MsgBox(FmtMessage(CustomMessage('ShareFailed'), [Log]), mbError, MB_OK);
  end;
end;
