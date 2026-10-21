# ADR-0030 — Dossier de location : agrégat `Booking`, machine à états unique et chronologie métier

- **Statut** : Acceptée
- **Date** : 2026-10-21
- **Source** : [conception phase 1](../conception/phase-1-exploitation.md) §2 et §3, [spécifications](../SPEC.md) §6 et §14, [prompt](../PROMPT.md) §6 (invariants), [ADR-0016](0016-numerotation-continue-table-de-compteurs.md), [ADR-0025](0025-moteur-de-disponibilite-cache-et-ecriture-sous-verrou.md), [ADR-0027](0027-personnes-dedoublonnage-et-anonymisation.md)

## Contexte

Les cinq canaux (site web, WhatsApp, téléphone, comptoir, Booking.com) produisent le même objet : un dossier de location qui vit de la demande à la clôture. La conception (§2) fixe les statuts et les transitions ; il reste à décider la forme de l'agrégat, l'endroit où vit la table des transitions, la façon de tracer chaque étape (SPEC §14 « chronologie de chaque dossier ») et la manière dont le dossier s'appuie sur les modules Clients, Flotte, Tarification et Référentiel sans en dépendre.

## Décision

### L'agrégat

- `Booking` (schéma `bookings`, table temporelle) porte : l'agence ; le numéro lisible `LOC-AAAA-NNNNNN` ([ADR-0016](0016-numerotation-continue-table-de-compteurs.md)), pris dans la transaction de création ; le canal, sa provenance (`BookingAttribution` : source et paramètres UTM, type complexe) et la référence du canal (unique par canal, index unique filtré) ; la catégorie ; le « modèle garanti » (véhicule réservé précisément, qui est aussi le véhicule affecté) ; le véhicule affecté et, le cas échéant, la **dérogation de conformité** (auteur, instant, motif) ; les lieux et instants prévus et réels (UTC, règles calendaires en heure de Tunis) ; les échéances du statut (blocage de la demande, option, paiement) ; le payeur (`CustomerId`) et les conducteurs (`PersonId`, conducteur principal obligatoire sauf en `Draft`, second conducteur facultatif) ; les options choisies, le code promo et les notes ; la version de devis courante et ses totaux ; le motif du statut ; la `rowversion`.
- Les autres modules ne sont connus que par identifiants et par leurs contrats (`ICustomersQueries`, `ICustomerRegistration`, `IFleetQueries`, `IPricingService`, `IReferentialQueries`) ; aucune clé étrangère vers leurs tables.
- L'agrégat ne vérifie **jamais** la disponibilité lui-même : l'application appelle le garde sous verrou avant toute commande qui consomme de la capacité, puis l'agrégat dans la même transaction ([ADR-0032](0032-protocole-ecriture-dossiers-et-taches.md)).

### Une seule table des transitions

- `BookingStateMachine` (domaine) déclare, pour chaque commande, les statuts d'où elle part. L'agrégat la consulte à chaque commande (toute autre transition renvoie `booking.invalid_transition`) ; les écrans proposent les actions qu'elle permet, croisées avec les permissions de l'utilisateur. Les tests de domaine parcourent exhaustivement le produit commandes × statuts.
- Règles en plus de la table, dans l'agrégat : en `InProgress`, `Amend` n'accepte qu'une prolongation (retour plus tard, le reste inchangé) et `AssignVehicle` est un échange de véhicule avec motif ; `MarkNoShow` n'est possible qu'après le départ prévu plus `bookings.no_show_delay` ; `Expire` exige que l'échéance soit atteinte ; `RequestInformation` et `ProvideInformation` exigent un blocage encore valable.
- **Motifs obligatoires** : refus, annulation, non-présentation, remise manuelle, échange de véhicule en cours de location, dérogation de conformité ; une demande d'informations combine un motif prédéfini (`InformationRequestReason`) et le message au client. Un motif fait au plus 500 caractères. Les motifs posés par la plateforme sont des clés (`booking.reason.customer_check`) traduites à l'affichage.

### Chronologie métier

- Chaque commande ajoute une ligne `BookingEvent` (ajout seul, numérotée par dossier) : type, statut avant et après, instant UTC, auteur (utilisateur, client, canal ou `system:*`), canal, motif et quelques faits en JSON (échéances en ISO 8601, montants en notation invariante, version de devis, champs modifiés par un avenant). L'écran traduit les types et les faits ; la donnée reste indépendante de la langue.
- Chaque commande incrémente `LastEventSequence` : la ligne du dossier change donc avec chaque ligne de chronologie et sa `rowversion` avec elle ; deux commandes concurrentes sur un même dossier ne peuvent pas réussir toutes les deux (la seconde reçoit `booking.concurrent_update`).
- Chaque commande émet un événement de domaine (outbox) ; en plus, chaque changement de statut, création comprise, publie dans la même transaction le message de contrat `BookingStatusChanged` : point d'extension de la capture de paiement (phase 2), des e-mails et des canaux. Les gestionnaires sont idempotents.

### Contrôles des conducteurs

- **Contrôle client interne** (liste noire, `ICustomersQueries.IsBlacklistedAsync`) : à la création rapide (erreur générique `booking.customer_check_failed`), à la validation d'une demande (refus avec le motif générique, sans autre détail) et au départ. Il n'est jamais montré au demandeur ; l'agent ne voit qu'un « contrôle client » non concluant.
- **Éligibilité** (âge, ancienneté du permis, documents valables jusqu'au retour, permis international) : bloquante à la validation d'une demande et au départ (`booking.drivers_not_eligible`), affichée sans bloquer à la création rapide (l'agent vérifie les documents au comptoir).

### Annulation et clôture

- Politique de l'agence : gratuite jusqu'à J-`bookings.cancellation_free_until_days` (jours calendaires de Tunis), ensuite `bookings.cancellation_fee_percent` du total TTC ; sans frais pour une demande, une option ou un dossier en attente de paiement. Les frais calculés sont figés sur le dossier ; les offrir est tracé (permission `bookings.discount`).
- `Closed` garde le tampon de préparation comme `Returned` (ADR-0025). La clôture transmet au module Clients la date de fin de location de chaque conducteur (`RecordRentalClosedAsync`, conservation des images de pièces) ; la tâche de purge des images saute les personnes qui ont un dossier ouvert (`IBookingsQueries.HasOpenBookingsAsync`).

## Conséquences

- Une seule source de vérité pour les transitions : l'agrégat, les écrans, les tests et la documentation lisent la même table.
- La chronologie suffit à expliquer un dossier sans lire les tables temporelles ; celles-ci restent la preuve technique ([ADR-0018](0018-journal-audit-ajout-seul.md)).
- Une personne anonymisée reste référencée par son identifiant ; les écrans affichent « personne anonymisée ».
- Les dérogations de conformité exigent la permission `bookings.compliance.waive` (gérant) et un motif ; elles sont visibles sur le dossier et dans le planning.
- Ajouter un statut ou une commande impose de compléter la table, les libellés FR/EN et les tests exhaustifs ; un test d'architecture vérifie les libellés.

## Alternatives écartées

- **Event sourcing du dossier** : reconstruction coûteuse pour les listes, le planning et le calcul d'occupation ; la chronologie en ajout seul et les tables temporelles donnent la traçabilité demandée.
- **Un type par statut (pattern État)** : la table déclarative est plus courte, se teste exhaustivement et alimente directement les écrans.
- **Journal d'audit générique comme chronologie** : il trace des écritures techniques en anglais ; la chronologie est métier, lisible par l'agent et traduite.
- **Clés étrangères vers les tables des autres modules** : contraires aux frontières de modules ([ADR-0012](0012-dbcontext-unique-et-frontieres-de-modules.md)) ; les contrats suffisent.
