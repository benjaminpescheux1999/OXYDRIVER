# Prerequis OXYDRIVER (complet)

Document de reference pour preparer un poste/client et garantir le bon fonctionnement de l'outil OXYDRIVER, de ses synchronisations API, de l'acces SQL, et des fonctions optionnelles (Cloudflare, SFTP).

## 1) Prerequis materiels (poste OXYDRIVER)

Minimum recommande pour un usage fluide :

- CPU: 2 coeurs (x64)
- RAM: 4 Go minimum (8 Go recommande)
- Espace disque libre: 1 Go minimum (2 Go recommande pour logs/updates)
- Connectivite reseau stable vers API, SQL et services externes eventuels

Pourquoi:

- OXYDRIVER maintient des services locaux (sync API, gateway locale, tunnel selon mode), ce qui demande un minimum de ressources continues.

## 2) Prerequis systeme (Windows)

- OS cible: Windows 10/11 64 bits
- Version minimale conseillee: Windows 10 22H2
- Horloge systeme synchronisee (NTP) recommande

Pourquoi:

- L'application est une app WPF `net8.0-windows`, avec stockage protege DPAPI et services reseau locaux.

## 3) Prerequis logiciels

## Poste OXYDRIVER

- .NET Desktop Runtime 8 (x64) si deploiement framework-dependent
- Acces en ecriture a `C:\ProgramData\OXYDRIVER` (settings, binaires auxiliaires, cache)
- Certificats systeme a jour (TLS sortant)

Pourquoi:

- Le parametrage et des secrets locaux sont stockes/proteges dans ProgramData.
- Les appels API, telechargements et tunnels reposent sur TLS.

## Serveur API OxyRest

- Node.js LTS (recommande: Node 20+)
- npm (installation/build)
- SQLite local accessible en ecriture (fichier DB OxyRest)
- Variables d'environnement securisees (`ADMIN_API_KEY`, `TOKEN_PEPPER`, etc.)

Pourquoi:

- OxyRest est une API Node/TypeScript avec persistence SQLite.

## 4) Prerequis SQL Server

- Instance SQL Server reachable depuis le poste OXYDRIVER
- Port SQL ouvert (par defaut TCP 1433, ou port personnalise de l'instance)
- SQL Browser/UDP 1434 seulement si necessaire pour instance nommee (optionnel selon configuration)
- Login SQL configure dans OXYDRIVER avec droits suffisants pour:
  - lister les bases (`master.sys.databases`)
  - creer/modifier login runtime si active
  - creer user dans les bases cibles
  - appliquer des `GRANT SELECT/UPDATE` sur objets exposes

Pourquoi:

- OXYDRIVER configure et applique des droits techniques runtime en fonction du catalogue de fonctionnalites expose.

## 5) Prerequis reseau et ports

Les flux ci-dessous doivent etre autorises par firewall/proxy.

## Flux obligatoires

1. OXYDRIVER -> OxyRest API  
   - Port: TCP 8080 par defaut (ou 443 si API derriere HTTPS/reverse proxy)  
   - Usage: negotiate/sync/bind/rotate/updates

2. OXYDRIVER -> SQL Server  
   - Port: TCP 1433 (ou port SQL configure)  
   - Usage: lecture/ecriture SQL selon droits exposes

3. OXYDRIVER (local)  
   - `127.0.0.1:<LocalPort>` (par defaut 5179)  
   - Usage: gateway locale pour le proxy applicatif  
   - Note: loopback local, pas d'ouverture internet directe requise.

## Flux optionnels (selon mode)

4. Mode `CloudflareAuto` (si actif)  
   - OXYDRIVER -> `github.com` (telechargement `cloudflared.exe`)  
   - OXYDRIVER -> reseau Cloudflare (tunnel sortant, typiquement TLS 443)  
   - Pourquoi: exposition securisee du service local via tunnel.

5. Updates SFTP (si activees cote API)  
   - OXYDRIVER/API -> serveur SFTP  
   - Port: TCP 22 (ou port SFTP personnalise)  
   - Pourquoi: distribution de binaires de mise a jour.

## 6) Prerequis de securite

- Definir des secrets forts:
  - `ADMIN_API_KEY`
  - `TOKEN_PEPPER`
  - secret JWT espace client (si utilise)
- Interdire les valeurs par defaut en production
- Restreindre l'acces aux endpoints admin
- Sauvegarder la base OxyRest (SQLite) regulierement
- Utiliser HTTPS/TLS entre composants des que possible

Pourquoi:

- Ces secrets conditionnent l'integrite des tokens, des resets admin et des flux clients.

## 7) Prerequis fonctionnels OXYDRIVER (parametrage initial)

Dans l'onglet Parametrage, les champs suivants doivent etre valides:

- URL API
- Cle d'acces (token)
- Hote SQL / mode auth / identifiants SQL
- Port local (par defaut 5179)
- Mode d'exposition (`CloudflareAuto` ou `ManualUrl`)
- URL manuelle si `ManualUrl`
- Parametres SFTP seulement si votre exploitation l'utilise

Pourquoi:

- Sans ces valeurs, la synchro API, l'application de droits SQL et l'exposition des services ne peuvent pas demarrer correctement.

## 8) Compatibilite versions / technologies

- Client OXYDRIVER: .NET 8 WPF (`net8.0-windows`)
- API OxyRest: Node.js + TypeScript
- Base API: SQLite
- Base metier: Microsoft SQL Server

Bonnes pratiques:

- Garder API et client sur des versions compatibles (sync negotiate/sync)
- Valider le catalogue de fonctionnalites apres upgrade
- Tester les droits SQL sur un environnement de preproduction

## 9) Checklist de validation avant mise en production

- [ ] Poste Windows conforme (version, ressources, droits ProgramData)
- [ ] Runtime .NET 8 installe (si necessaire)
- [ ] API OxyRest demarree et healthcheck OK
- [ ] Variables d'environnement de prod configurees (sans valeurs par defaut)
- [ ] SQL accessible (port + credentials + droits techniques)
- [ ] Synchronisation API OXYDRIVER reussie
- [ ] Application des droits SQL validee (lecture/ecriture selon catalogue)
- [ ] Mode d'exposition valide (`CloudflareAuto` ou `ManualUrl`)
- [ ] Flux reseau/firerwall testes (API, SQL, SFTP optionnel)
- [ ] Procedure "mot de passe oublie" et reset admin testee
- [ ] Sauvegarde/restauration SQLite OxyRest testee

## 10) Points d'attention / incidents frequents

- Fichier binaire verrouille pendant build (application encore ouverte)
- Port SQL non ouvert ou instance nommee mal resolue
- Blocage proxy/firewall sortant vers API/Cloudflare/SFTP
- Secrets API laisses en mode dev
- Droits SQL insuffisants pour provisioning runtime

---

Si tu veux, je peux te faire une version 2 de ce document avec:

- une section "Prerequis par profil" (poste client, serveur API, DBA),
- une matrice de flux reseau source/destination/port/protocole,
- et une annexe "plan de recette" preproduction.

