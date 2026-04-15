# OXYDRIVER - Setup Production (Service + UI)

Ce document decrit un deploiement production fiable avec:

- **service Windows** en arriere-plan (`--service`)
- **UI tray** separee pour exploitation utilisateur

## 1) Build / publish

Publier en Release (exemple):

```powershell
dotnet publish "d:\repos\OXYDRIVER\src\Oxydriver.App\Oxydriver.App.csproj" -c Release -o "C:\Program Files\OXYDRIVER"
```

Verifier la presence de:

- `C:\Program Files\OXYDRIVER\OXYDRIVER.exe`
- `C:\Program Files\OXYDRIVER\Assets\OXYDRIVER.ico`

## 2) Installer le service Windows

Ouvrir PowerShell **en administrateur**:

```powershell
cd "d:\repos\OXYDRIVER\src\deploy"
.\install-service.ps1 -ExePath "C:\Program Files\OXYDRIVER\OXYDRIVER.exe"
```

Par defaut:

- Nom service: `OXYDRIVERService`
- Demarrage: `auto`
- Politique de reprise: restart automatique sur echec

## 3) Exploitation courante

- Redemarrer le service:

```powershell
.\restart-service.ps1
```

- Desinstaller le service:

```powershell
.\uninstall-service.ps1
```

## 4) Logs runtime service

Le service ecrit un log de run ici:

- `C:\ProgramData\OXYDRIVER\logs\service-runtime.log`

Ce log contient:

- demarrage/arret runtime
- sync API ok/refusee
- fallback de port local
- erreurs de cycle

## 5) Workflow de mise a jour production

1. Stopper le service:

```powershell
Stop-Service OXYDRIVERService
```

2. Deployer la nouvelle version (publish/copie binaire).
3. Demarrer le service:

```powershell
Start-Service OXYDRIVERService
```

4. Verifier:
   - statut service `Running`
   - log `service-runtime.log`
   - UI OXYDRIVER (si ouverte) et statuts Sync/Tunnel/SQL.

## 6) Notes importantes

- UI et service ont des mutex distincts (pas de conflit instance unique).
- Le mode service ne demande pas de login UI.
- L'UI reste utile pour le parametrage et le support, mais le moteur tourne en service.

