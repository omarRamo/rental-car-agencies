# Installation locale proche de la production (sans Docker)

Pour faire tourner la plateforme sur un poste Windows avec un SQL Server déjà installé (SQL Server Express suffit), au plus
près de la production :

| Production (Azure) | Installation locale |
|---|---|
| Build Release publié | Build Release publié dans `app\` |
| Azure SQL, migrations appliquées par le bundle avant le déploiement | SQL Server local, migrations appliquées par le même bundle (`efbundle.exe`), jamais au démarrage |
| App Service Linux (Kestrel) | Kestrel, en arrière-plan |
| Blob Storage | Azurite (émulateur de Blob Storage), données conservées dans `azurite\` |
| E-mails par Azure Communication Services | Fichiers `.eml` dans `mail\` |
| Clic to Pay, WhatsApp, modèle d'IA réels | Banque simulée (`/dev/clictopay`), WhatsApp simulé, modèle scripté |
| Pas de données de démonstration | Données de démonstration chargées au premier démarrage |

L'application tourne avec l'environnement « Development » : c'est le seul qui accepte ces substituts. Les environnements
« Staging » et « Production » exigent les vrais services Azure et refusent les partenaires simulés.

## Prérequis

- SDK .NET 10 (version de `global.json`) et `dotnet tool restore` (outil `dotnet-ef`).
- SQL Server 2019 ou plus récent, accessible en authentification Windows par l'utilisateur qui lance les scripts.
- Node.js (Azurite est lancé par `npx`).
- Le certificat HTTPS de développement : `dotnet dev-certs https --trust` une fois.

## Utilisation (PowerShell)

```powershell
# Installer ou mettre à jour : publication, bundle de migrations, base créée ou migrée
.\deploy\local\install.ps1 -SqlServer 'LOQ-OMAR\SQLEXPRESS' -Database 'CarRental'

# Démarrer (Azurite puis l'application, en arrière-plan) ; le premier démarrage charge les données de démonstration
.\deploy\local\start.ps1 -SqlServer 'LOQ-OMAR\SQLEXPRESS' -Database 'CarRental'

# Arrêter
.\deploy\local\stop.ps1
```

- Vitrine : https://localhost:7180/fr — back office : https://localhost:7180/admin.
- Comptes `gerant@demo.local`, `agent@demo.local`, `comptable@demo.local` : mot de passe et clé de double authentification
  dans `src\CarRental.Web\appsettings.Development.json` (section `Seed`).
- Les données vivent dans le dossier `carrental-local` à côté du dépôt (paramètre `-DataRoot` pour un autre dossier) :
  `app\` (application publiée), `azurite\` (fichiers), `mail\` (e-mails), `logs\` (journaux de l'application et d'Azurite).
- Après un `git pull`, relancer `stop.ps1`, `install.ps1` puis `start.ps1` : la nouvelle version est publiée, les nouvelles
  migrations sont appliquées, les données sont conservées.
- Repartir de zéro : arrêter, supprimer la base (`DROP DATABASE`) et le dossier `azurite\`, puis réinstaller.
