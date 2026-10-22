# ADR-0066 — Revue de sécurité, passe 2 : journaux sans jeton, Turnstile lié au site, documents jamais conservés, santé du bot

- **Statut** : Acceptée
- **Date** : 2026-10-22
- **Source** : [revue OWASP d'octobre 2026](../securite/revue-owasp-2026-10.md), section 7 (passe 2), [ADR-0062](0062-origine-cloudflare-et-revue-de-securite.md) (passe 1) ; précise l'[ADR-0037](0037-vitrine-anti-abus-et-mesure-entonnoir.md) (Turnstile), l'[ADR-0049](0049-tunnel-sessions-devis-figes-garantie-de-prix.md) et l'[ADR-0051](0051-ma-reservation-acces-et-liaison-dossier-paiement.md) (jetons des adresses), l'[ADR-0041](0041-depart-retour-etat-des-lieux-et-contrat.md) et l'[ADR-0058](0058-documents-de-facturation-pdf-envoi-et-facture-electronique.md) (mise en cache des documents), l'[ADR-0060](0060-alertes-et-telemetrie-de-sante.md) (entrée `whatsapp`)

## Contexte

La passe 2 de la revue a repris, après la fusion du tunnel et de « Ma réservation » (2C), de l'agent IA WhatsApp (4B) et de la facturation (3A), les points laissés « à revoir » par la passe 1. Les contrôles annoncés tiennent pour l'essentiel (jetons de 256 bits hachés, codes salés, comparaisons en temps constant, usage unique, réponses identiques, outils du bot en lecture seule, écriture unique des factures), mais quatre décisions structurantes manquaient ou contredisaient la sécurité : les jetons des adresses publiques atteignaient les journaux, la vérification Turnstile ignorait le site où le défi avait été résolu, les documents à données personnelles restaient dans le cache du navigateur, et le bot n'avait plus d'entrée de santé depuis qu'il a quitté l'outbox.

## Décision

### Aucun jeton d'adresse dans les journaux

- Les journaux exportés (OpenTelemetry, Application Insights) **n'incluent plus les portées** (`IncludeScopes = false`) : la portée de requête d'ASP.NET Core porte le chemin brut (`RequestPath`), donc le jeton d'une étape du tunnel ou d'un lien « Ma réservation », dans chaque journal écrit pendant la requête. Un journal reste relié à sa requête par le contexte de trace, dont le chemin est déjà écrit `{token}` (`SecretPaths`).
- Tout journal qui cite un chemin le passe par `SecretPaths.Redact` (refus d'une limite de débit publique).
- Le niveau `Microsoft.AspNetCore` reste à `Warning` (journal « Request starting » avec l'adresse complète sinon).

### Turnstile : le site, l'action, et jamais une clé de test en production

- La réponse de `siteverify` doit porter **l'action du formulaire et le nom d'hôte de la requête qui le soumet** (filtré par `HostFiltering`, comparé sans casse) ; un nom d'hôte absent ou différent refuse (`hostname-mismatch`). Les domaines du widget Turnstile chez Cloudflare doivent donc lister exactement les noms du site (domaine, `www`, recette).
- Une réponse d'une **clé de test** de Cloudflare (`metadata.result_with_testing_key`) n'a ni action ni vrai nom d'hôte et accepte un jeton que tout le monde peut écrire : acceptée **hors production seulement** (`Turnstile:AcceptTestingKeys`, fixé par l'environnement, jamais par la configuration), refusée en production avec un journal d'erreur. Le développement avec les clés de test fonctionne de nouveau (l'action manquante faisait tout refuser).

### Documents à données personnelles : jamais conservés par le navigateur

- Les PDF des factures et des avoirs, les fichiers des sinistres, les contrats de location et les signatures répondent **`no-store, private`**, comme les pièces d'identité, la fiche de désignation, l'export des données d'une personne et les exports Excel. Cela remplace le `private, no-cache` avec `ETag` de l'ADR-0058 et de l'ADR-0041 : sur une tablette partagée du comptoir, le cache disque du navigateur gardait l'acheteur, le conducteur ou la signature au-delà de la session.
- Les photos des véhicules (départ, retour) gardent leur cache privé revalidé (nombreuses, sans donnée personnelle).

### Entrée de santé `whatsapp`

- Depuis l'ADR-0054, les tours du bot ne passent plus par l'outbox : l'entrée `outbox` ne voit plus un bot bloqué. L'entrée **`whatsapp`** (Degraded, jamais Unhealthy) signale une conversation dont un message client attend le bot plus longtemps qu'un tour ne peut durer : silence, tous les délais de nouvelle tentative et dix minutes d'appels au modèle, **vingt minutes au moins**. Lue sur l'index filtré de la file, publiée et alertée par la règle générique `health-degraded` sans changer l'infrastructure. Les envois restent surveillés par l'entrée `outbox`.

### Adresses de retour

- `LocalUrls.IsLocal` suit enfin la règle d'`IsLocalUrl` d'ASP.NET Core jusqu'aux caractères de contrôle : un navigateur retire une tabulation ou un saut de ligne, et `/%09/exemple.com` devenait `//exemple.com` (redirection ouverte du lien « Continuer » de la double authentification, erreur 500 du sélecteur de langue anonyme).

## Conséquences

- Les journaux d'Application Insights perdent les propriétés `RequestPath`, `RequestId` et `ConnectionId` des portées : la requête d'un journal se retrouve par son `operation_Id`.
- Un widget Turnstile mal configuré (un domaine du site absent) refuse les soumissions de ce domaine : liste des domaines à vérifier avant l'ouverture ([points ouverts](../points-ouverts.md)).
- Une facture, un contrat ou une signature se télécharge de nouveau à chaque ouverture (quelques dizaines de kilo-octets).
- Une instance sans serveur de tâches (`BackgroundJobs:RunServer=false`, procédure de restauration du runbook) rend l'entrée `whatsapp` dégradée tant que des clients écrivent au bot : c'est voulu, personne ne leur répond.

## Alternatives écartées

- **Filtrer la seule portée de requête** (processeur de journaux, niveau `None` pour `Microsoft.AspNetCore.Hosting.Diagnostics`) : les portées d'OpenTelemetry ne se modifient pas dans un processeur, et le niveau d'une catégorie se change trop facilement pour un diagnostic ; l'application n'ouvre elle-même aucune portée.
- **Comparer le nom d'hôte de Turnstile à une liste de configuration** : une liste de plus à tenir, alors que l'hôte de la requête est déjà filtré.
- **Garder l'`ETag` des documents** : la revalidation suppose que le navigateur garde le fichier, ce qu'on veut justement éviter.
- **Remettre les tours du bot dans l'outbox pour l'entrée `outbox`** : contraire à l'ADR-0054 (un appel au modèle de plusieurs secondes bloquerait toute l'outbox).
