# ADR-0032 — Écriture des dossiers : un pipeline unique sous garde, tâches d'expiration et d'escalade, contrats du module

- **Statut** : Acceptée
- **Date** : 2026-10-21
- **Source** : [conception phase 1](../conception/phase-1-exploitation.md) §1 à §3, [spécifications](../SPEC.md) §6, [prompt](../PROMPT.md) §6 (invariants 2, 6 et 11), [ADR-0013](0013-outbox-traitee-par-hangfire.md), [ADR-0016](0016-numerotation-continue-table-de-compteurs.md), [ADR-0017](0017-execution-cas-usage-portee-transactions.md), [ADR-0025](0025-moteur-de-disponibilite-cache-et-ecriture-sous-verrou.md)

## Contexte

Une quinzaine de cas d'usage écrivent des dossiers : création rapide, demande web, validation, demande d'informations, refus, confirmation d'option, paiement, annulation, non-présentation, avenant, affectation, propositions d'affectation, départ, retour, clôture, et deux tâches de fond. Chacun doit respecter le même protocole (verrou et recontrôle avant de consommer de la capacité, numéro dans la transaction, outbox, cache prévenu après le commit seulement) sans que l'un d'eux l'oublie. Les modules suivants (paiements 1F-a, départs et retours 1F-b, tunnel public phase 2) doivent pouvoir s'y brancher sans connaître les internes du module.

## Décision

### Le pipeline d'écriture (`BookingWriter`, interne au module)

- **Une unité de travail par commande**, rejouable après une erreur transitoire (stratégie d'exécution, [ADR-0017](0017-execution-cas-usage-portee-transactions.md)) : l'opération charge elle-même ce dont elle a besoin à l'intérieur ; une relecture repart de zéro.
- **Garde de disponibilité** ([ADR-0025](0025-moteur-de-disponibilite-cache-et-ecriture-sous-verrou.md)) dans la transaction pour chaque commande qui consomme de la capacité ou garde un véhicule : création rapide, `Submit`, `ProvideInformation`, `ConfirmOption`, avenant qui déplace l'occupation (les deux catégories sur un changement de catégorie), affectation et échange (`EnsureVehicleAssignableAsync`), départ (véhicule affecté, et capacité quand le véhicule part avant l'heure prévue). Les commandes qui libèrent ou constatent (refus, annulation, expiration, non-présentation, retour, clôture) ne recontrôlent pas ; la validation d'une demande non plus, puisque la demande occupe déjà sa capacité.
- **Numéro `LOC`** pris dans la même transaction ([ADR-0016](0016-numerotation-continue-table-de-compteurs.md)) : une écriture refusée par le garde annule aussi le numéro, la série reste sans trou.
- **Outbox** : événements de domaine et message `BookingStatusChanged` dans la même transaction ([ADR-0013](0013-outbox-traitee-par-hangfire.md)).
- **Cache** : les catégories touchées sont notifiées **après le commit**, jamais avant ni après une annulation.
- **Concurrence** : une modification concurrente du même dossier (`rowversion`) devient `booking.concurrent_update` ; un avenant revérifie que la version de devis et le statut n'ont pas changé depuis le calcul de son prix.
- **Permissions** vérifiées par chaque service (défense en profondeur, les pages déclarent les mêmes) : `requests.read`/`requests.manage` (file des demandes, validation, informations, refus), `bookings.read`/`bookings.manage` (dossiers, création, avenants, affectation, annulation, clôture), `bookings.discount` (remise manuelle, frais d'annulation offerts), `bookings.compliance.waive` (dérogation de conformité), `handovers.manage` (départ, retour) ; `payments.manage` peut aussi marquer un dossier payé.
- **Erreurs** : codes `booking.*` (et `availability.*` du garde) par `Result`, textes en français et en anglais ; un test d'architecture vérifie qu'aucun code n'en manque.

### Tâches de fond (Hangfire)

- `bookings.expire`, **chaque minute** : demandes dont le blocage est passé (`PendingValidation`, `InformationRequested`), options échues (`OptionHeld`), validations dont l'échéance de paiement est passée (`AwaitingPayment`) passent en `Expired` (invariant 11). Lots de 200, une unité de travail par dossier, acteur système, ligne de chronologie, cache prévenu ; idempotente (un dossier traité entre-temps est ignoré).
- `bookings.escalate`, **toutes les 5 minutes** : les demandes encore `PendingValidation` après `bookings.escalation_delay` reçoivent un drapeau `EscalatedAtUtc` (visible dans la file des demandes et le tableau de bord) et chaque gérant actif reçoit **un** e-mail qui les liste (numéro, canal, instants ; aucune donnée personnelle). Drapeaux et e-mails sont écrits dans la même transaction : une demande n'est signalée qu'une fois par période d'attente, même si l'exécution est rejouée ; la période recommence quand la demande revient en attente après une demande d'informations.
- **Paramètres** déclarés par le module (`BookingSettings`, par agence) : `bookings.information_request_extension` (24 h), `bookings.option_default_duration` (48 h), `bookings.payment_link_validity` (24 h), `bookings.no_show_delay` (120 min), `bookings.escalation_delay` (120 min), `bookings.cancellation_free_until_days` (3), `bookings.cancellation_fee_percent` (0,20), `bookings.validation_payment_mode` (`CaptureAtValidation`, ou `PaymentLinkAfterValidation` en mode de repli). Le blocage d'une demande (`bookings.hold_duration`) et le tampon (`fleet.preparation_buffer`) sont des paramètres de plateforme du Référentiel.

### Ce que voient les autres modules

- `IBookingsQueries` (lecture fraîche, sans cache) : résumé d'un dossier par identifiant ou par numéro, dossiers d'une personne, « a des dossiers ouverts », tableau du jour (départs et retours), demandes en attente, planning, retours en retard, dossiers sans véhicule qui partent bientôt, manques de capacité.
- `IBookingRequests` (tunnel public et bot, phase 2) : devis verrouillé et sa relecture par code, brouillon, soumission, demande complète en une fois, complément d'informations fourni. Les conducteurs sont enregistrés auparavant par `ICustomerRegistration` ; le payeur est le client particulier du conducteur principal par défaut.
- `IBookingPaymentStatus.MarkPaidAsync` (module Paiements) : `AwaitingPayment` → `Confirmed`, idempotent.
- **Point d'extension de la capture** (phase 2) : un gestionnaire de `BookingStatusChanged` (statut `Confirmed` venant de `PendingValidation`) capture la pré-autorisation ; en mode de repli, le lien de paiement est envoyé sur `AwaitingPayment` et le paiement reçu appelle `MarkPaidAsync`.
- **Départ, retour, clôture** (1F-b) : `RentalLifecycleService` (API interne au module, utilisée par les écrans du module) fait la transition, la chronologie, l'outbox et le cache ; l'appelant branche son propre travail (état des lieux, photos, signature, contrat, frais de retour, caution, solde) dans l'étape `alongside`, exécutée **dans la même unité de travail** juste après la transition : si elle échoue, rien n'est enregistré. Le départ vérifie le contrôle client, l'éligibilité jusqu'au retour et le véhicule sous verrou ; la clôture informe le module Clients pour chaque conducteur.

### Données de démonstration

- L'étape de seed `Demo` (ordre 700) crée une quarantaine de dossiers de l'agence de démonstration, sur les 30 jours passés et les 60 suivants, dans tous les statuts. Les dossiers à venir passent par les services ci-dessus ; l'historique est rejoué sur l'agrégat avec ses instants passés, dans le même pipeline et sous le même garde (qui protège l'occupation à partir de maintenant) ; le passé est disposé véhicule par véhicule sans chevauchement. Un test d'intégration vérifie qu'aucun véhicule ni aucune catégorie n'est jamais surréservé.

## Conséquences

- Un nouveau cas d'usage d'écriture passe par `BookingWriter` et hérite du protocole ; un oubli du garde se voit à la revue (liste ci-dessus) et dans les tests d'intégration (deux demandes simultanées sur le dernier véhicule).
- Les expirations sont faites au plus une minute après l'échéance : pendant cette minute, la demande échue compte encore dans l'occupation. Au pire, une vente possible est refusée un instant ; jamais la même capacité n'est vendue deux fois. L'expiration libère la capacité dès son commit (cache prévenu).
- Les e-mails d'escalade sont rédigés par l'hôte web (ressources français et anglais), comme les alertes d'échéance de la flotte.

## Alternatives écartées

- **Expiration paresseuse à la lecture** : la capacité serait libérée sans ligne de chronologie ni message, et les écrans devraient tous interpréter les échéances.
- **Une tâche Hangfire programmée par dossier** : à reprogrammer à chaque avenant ou demande d'informations, fragile si la tâche se perd ; un balayage chaque minute est simple et idempotent.
- **Laisser chaque service gérer sa transaction** : le protocole (verrou, numéro, outbox, cache après commit) serait recopié quinze fois.
- **Exposer `RentalLifecycleService` dans les contrats** : les départs et retours font partie du même module ; les autres modules ne voient que les contrats.
