# ADR-0022 — Photos de la flotte : stockage privé, photos de catégorie servies par l'application

- **Statut** : Acceptée
- **Date** : 2026-10-20
- **Source** : [conception phase 1](../conception/phase-1-exploitation.md) §5, [ADR-0004](0004-hebergement-app-service-b1-cloudflare.md), [ADR-0005](0005-vente-par-categorie-acriss.md)

## Contexte

Le site public vend des catégories « ou similaire » ([ADR-0005](0005-vente-par-categorie-acriss.md)) et doit afficher la photo de chaque catégorie, exposée aux autres modules par `VehicleCategoryInfo.PhotoUrl`. Les photos des véhicules (état, carrosserie, plaque) ne concernent que le back-office. Le stockage Azure est privé, sans accès anonyme ; aucune bibliothèque de traitement d'image n'est retenue (licences, poids).

## Décision

- **Conteneur privé `fleet`** (accès public `None`), créé au premier envoi par `IFileStorage`. Chemins : `categories/{catégorie}/{empreinte}.{ext}` pour une catégorie, `vehicles/{véhicule}/{photo}.{ext}` pour un véhicule.
- **Validation à l'envoi** : 10 Mo au plus (lecture bornée), format reconnu par sa **signature** (JPEG, PNG ou WebP) ; le type de contenu et l'extension viennent de la signature, jamais du nom ni du type annoncés par le navigateur ; empreinte SHA-256 conservée. Au plus 12 photos par véhicule ; aucun envoi sur un véhicule vendu.
- **Photo de catégorie** : `GET /media/categories/{id}`, **anonyme**, flux lu depuis le stockage par l'application, `Cache-Control: public, max-age=86400`, `ETag` égal à l'empreinte et réponse 304 sur `If-None-Match`. `PhotoUrl` vaut `/media/categories/{id}?v={empreinte courte}` : l'adresse change avec la photo, ce qui permet la mise en cache par le navigateur et par Cloudflare ([ADR-0004](0004-hebergement-app-service-b1-cloudflare.md)). Pas de photo, ou catégorie supprimée : 404.
- **Photo de véhicule** : `GET /media/vehicles/{véhicule}/photos/{photo}`, réservée aux utilisateurs connectés qui détiennent la permission `fleet.read` (sinon renvoi vers la connexion ou refus d'accès), `Cache-Control: private, no-cache` et `ETag`.
- **Cohérence base et stockage** : le fichier est écrit avant la ligne en base ; si l'enregistrement échoue, le fichier nouvellement écrit est supprimé ; le fichier remplacé ou retiré n'est supprimé qu'après la validation, au mieux (un échec est journalisé et laisse un orphelin sans conséquence fonctionnelle).

## Conséquences

- Aucune URL de stockage ni signature d'accès partagé (SAS) n'est exposée ; les photos de véhicule ne sont jamais accessibles hors du back-office.
- Les photos de catégorie passent par l'App Service au premier affichage, puis par les caches ; volume faible (cinq à dix catégories).
- Les images ne sont ni redimensionnées ni réencodées : l'agence doit envoyer des visuels déjà adaptés au web, et les métadonnées (EXIF) d'une photo de catégorie restent publiques. Des miniatures et le nettoyage des métadonnées pourront venir avec une bibliothèque compatible (`TODO`).
- Le conteneur `fleet` est à déclarer dans l'infrastructure (Bicep) et dans la liste des conteneurs créés au démarrage ; d'ici là, il est créé au premier envoi.
- Les orphelins éventuels du stockage pourront être purgés par une tâche de rapprochement (`TODO`).

## Alternatives écartées

- **Conteneur à accès public** : expose aussi les photos de véhicule et interdit de changer de stockage sans casser les adresses.
- **URL signées (SAS) à durée limitée** : adresses instables, incompatibles avec un `PhotoUrl` durable et le cache.
- **ImageSharp ou équivalent** : licence non permissive pour un usage commercial.
- **Photos en base** : alourdit la base et les sauvegardes, sans bénéfice.
