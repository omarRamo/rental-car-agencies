# ADR-0001 — Monolithe modulaire .NET 10 dans un monorepo

- **Statut** : Acceptée
- **Date** : 2026-10-20
- **Source** : [spécifications](../SPEC.md), sections 1 et 12

## Contexte

L'agence a 30 véhicules et une seule agence au lancement. Le besoin demande un back end .NET, un monorepo et une seule application web si possible, avec un coût d'hébergement minimal.

## Décision

Une seule application ASP.NET Core sur .NET 10 (LTS) sert la vitrine, le back office (`/admin`), les API d'intégration, les webhooks et les tâches de fond. Le code est découpé en modules métier : Flotte, Tarifs, Réservations, Clients, Paiements, Facturation, Caisse et dépenses, Canaux, Messagerie, Rapports, Identité et audit.

Projets prévus :

- `CarRental.Web` : hôte (vitrine SSR, back office, API, webhooks, tâches)
- `CarRental.Application` : cas d'usage par module (commandes, requêtes, règles)
- `CarRental.Domain` : agrégats, objets valeur (`Money` en TND, `Period`), événements, invariants
- `CarRental.Infrastructure` : EF Core / SQL, Blob, e-mail, Clic to Pay, WhatsApp, Claude, TTN, Booking.com

## Conséquences

- Un seul build, un seul déploiement, un seul langage.
- Des tests d'architecture vérifient qu'aucun module ne lit les tables d'un autre ; c'est ce qui garde le monolithe modulaire.
- Le modèle est prêt pour plusieurs agences, mais une seule instance tourne aujourd'hui (voir [ADR-0004](0004-hebergement-app-service-b1-cloudflare.md)).

## Alternatives écartées

- Microservices ou applications séparées : coût et complexité d'exploitation sans rapport avec la taille de la flotte.
