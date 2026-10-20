# ADR-0004 — App Service B1 Linux en France Central, Cloudflare Free devant

- **Statut** : Acceptée
- **Date** : 2026-10-20
- **Source** : [spécifications](../specifications.md), section 12

## Contexte

L'objectif est une infrastructure Azure d'environ 20 à 35 USD par mois hors usage variable (devise à confirmer, voir [points ouverts](../points-ouverts.md)), toujours active, proche de la clientèle (Tunisie et diaspora en Europe).

## Décision

- **App Service Linux B1** (1 cœur, 1,75 Go, toujours actif, certificat géré gratuit) en **France Central**.
- **Cloudflare Free** devant : DNS, CDN, anti-DDoS, Turnstile.
- Services associés : Key Vault (secrets lus par identité managée), Application Insights (OpenTelemetry, quota gratuit), Azure Communication Services Email.
- **Recette** sur App Service F1 gratuit et l'offre gratuite Azure SQL si elle existe toujours.
- Infrastructure décrite en **Bicep** ; livraison par **GitHub Actions** (build, tests, analyse, migrations EF par bundle, déploiement zip).

## Conséquences

- Pas d'emplacements de déploiement sur B1 : une mise en production redémarre l'application quelques secondes, donc elle se fait la nuit.
- Une seule instance. Passer à plusieurs exigerait Azure SignalR et un cache partagé.
- Héberger en France est un transfert de données hors de Tunisie : l'autorisation INPDP est obligatoire avant la production (voir [ADR-0010](0010-conformite-ttn-inpdp.md)).
