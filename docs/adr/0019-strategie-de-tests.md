# ADR-0019 — Stratégie de tests : xUnit v3 sur VSTest, conteneurs partagés, Playwright

- **Statut** : Acceptée
- **Date** : 2026-10-20
- **Source** : [prompt](../PROMPT.md) section 13, flux `.github/workflows/ci.yml`

## Contexte

Les invariants reposent sur SQL Server (tables temporelles, `sp_getapplock`, déclencheurs, verrous de ligne) : une base en mémoire ou SQLite ne les reproduit pas. La CI lance les tests avec les options de VSTest (`--logger trx`, `--collect "XPlat Code Coverage"`, `--blame-hang-timeout`) et un fichier de paramètres de couverture. Le parcours de connexion avec double authentification et l'accessibilité se vérifient dans un vrai navigateur.

## Décision

- **Outils** : xUnit v3 exécuté par VSTest (`xunit.v3.mtp-off`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`, couverture `coverlet.collector`), Shouldly, FsCheck pour les tests par propriétés, NetArchTest pour l'architecture. `dotnet test` à la racine lance les cinq projets.
- **Pyramide** :
  - `CarRental.Domain.Tests` : objets valeur et agrégats, propriétés (`Money`, `Period`) ; objectif 90 % de couverture du domaine ;
  - `CarRental.Application.Tests` : services avec des doubles (`Fakes/`), matrice des droits, catalogue des permissions ;
  - `CarRental.Architecture.Tests` : couches, frontières de modules, conventions des pages et des ressources ;
  - `CarRental.Integration.Tests` : application réelle sur SQL Server et Azurite ;
  - `CarRental.E2E.Tests` : navigateur Chromium (Playwright) sur l'application réelle.
- **Conteneurs partagés** : un fixture d'assembly (`[assembly: AssemblyFixture]`) par projet d'intégration ou E2E démarre SQL Server 2022 et Azurite (Testcontainers, mêmes images que `docker-compose.yml`) et une base dédiée (`CarRental_Integration`, `CarRental_E2E`). `CarRentalAppFactory` (`tests/Shared`, lié dans les deux projets) démarre `Program` dans l'environnement `Testing`, migrations et données de démonstration comprises, avec un mot de passe et une clé TOTP de test.
- **Parallélisme** : les tests s'exécutent en parallèle sur la même base ; chacun isole ses données (noms uniques, identifiants neufs, exercices de numérotation propres à chaque test).
- **Connexion** : code TOTP calculé par `tests/Shared/Totp.cs` ; en HTTP, `Support/HttpSignIn` gère le jeton anti-falsification et les formulaires Blazor.
- **E2E** : Kestrel sur un port libre, Chromium installé par le fixture (déjà présent en CI avec ses dépendances système), un contexte de navigateur par test (390 px de large par défaut, `fr-FR`), analyse axe-core sans violation critique ou sérieuse, trace et capture d'écran des tests en échec dans `E2E_ARTIFACTS_DIR` (CI) ou `bin/.../e2e-artifacts`.

## Conséquences

- Docker est nécessaire pour les tests d'intégration et E2E, en local comme en CI ; une exécution complète prend une à deux minutes.
- Toute règle qui dépend de SQL Server se teste contre SQL Server ; aucun test n'utilise le fournisseur en mémoire.
- Un nouveau test E2E hérite de `BrowserTest` ; un nouveau test d'intégration reçoit `IntegrationHost` (client HTTP avec cookies, portée DI avec un acteur choisi).

## Alternatives écartées

- **Microsoft Testing Platform** : les options de la CI sont celles de VSTest.
- **Fournisseur EF en mémoire ou SQLite** : ni tables temporelles, ni verrous applicatifs, ni déclencheurs.
- **Base partagée lancée à la main (`docker compose`) pour les tests** : état non maîtrisé, échecs selon le poste.
