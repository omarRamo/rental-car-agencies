# Conception — Phase 5 (canaux et Booking.com)

Contrat commun aux agents de la phase 5. Il précise [SPEC.md](../SPEC.md) §10 (« Booking.com et gestionnaire de canaux ») et [PROMPT.md](../PROMPT.md) §9.3. En cas de doute, ces documents priment et l'écart va dans [points-ouverts.md](../points-ouverts.md).

**Fini quand** (PROMPT §15) : le banc de contrat passe et une réservation canal arrive confirmée dans le planning sans intervention.

## 1. Modèle interne unique

- **Canal** (`Channel`) : code (`BookingCom`, d'autres comparateurs plus tard), adaptateur utilisé, état (inactif, manuel, connecté), authentification entrante (clé d'API hachée ou empreinte de certificat client, liste d'adresses IP autorisées), commission (pourcentage, ou montant transmis par le canal, qui prime), mode de paiement par défaut (prépayé au canal ou payé sur place).
- **Correspondances** (`ChannelMapping`) : catégorie ↔ code ACRISS et code du canal ; lieu ↔ code du canal (aéroports TUN, NBE, MIR, DJE…) ; options, assurances et frais ↔ codes des listes OpenTravel Alliance, saisis par l'agence (aucune liste inventée).
- **Quotas et arrêts de vente** par canal × catégorie × période (et lieu si besoin) : véhicules réservés aux autres canaux (« garder 2 véhicules hors Booking.com en haute saison ») et arrêts de vente datés. Ils alimentent `IChannelAvailabilityPolicy` (contrat du module Bookings), si bien que le moteur de disponibilité les applique partout, jamais à part.
- **Tarifs** : la règle du canal du module Tarifs (parité, majoration ou grille propre) ; le module Canaux ne calcule aucun prix et publie ceux du moteur.

## 2. Adaptateurs

- `IChannelAdapter` : `PushAvailabilityAsync`, `PushRatesAsync`, `HandleInboundAsync` (disponibilité, création, modification, annulation, consultation), `MapBooking`. Le cœur parle un **format canonique** (requêtes et réponses en objets C#) ; chaque adaptateur traduit le format de son canal.
- **Booking.com** : aucune API publique ne permet de publier une flotte ; le format XML/API arrive avec le cahier des charges remis après l'acceptation fournisseur. **Aucun format n'est inventé** : tant qu'il n'est pas fourni, `ManualChannelAdapter` (saisie dans le back office avec la source « Booking.com », référence canal, prix brut, commission et net, mode de paiement ; rappel des arrêts de vente et des quotas à reporter sur l'extranet) et un adaptateur `Booking.com` vide marqué `TODO(spec)`.
- **Faux canal** (`FakeChannelAdapter`, développement et tests uniquement) : un format JSON de test, explicitement documenté comme tel, pour exercer tout le chemin entrant et sortant.

## 3. Flux

- **Entrant** `/api/channels/{canal}/...` : authentification (clé ou certificat, liste d'adresses IP), limitation de débit dédiée, journal de chaque message (`ChannelMessageLog` : direction, type, référence canal, statut, durée, extrait sans donnée sensible), idempotence par référence canal, réponse de disponibilité en moins d'une seconde depuis le cache.
- **Création** : la réservation arrive **confirmée** (règle des distributeurs), sous le verrou de disponibilité comme tout autre canal, avec la référence canal, le mode de paiement, le prix brut, la commission et le net. Elle apparaît au planning sans intervention ; les contrôles se font au comptoir. Une indisponibilité est refusée au canal (jamais de surréservation).
- **Modification et annulation** : appliquées au dossier (avenant ou annulation, motif « canal »), idempotentes.
- **Sortant** : à chaque changement de disponibilité, de tarif, de quota ou d'arrêt de vente pour un canal connecté, envoi des mises à jour (débouncées), plus une resynchronisation complète chaque nuit. Échec ⇒ file de rejeu avec délais croissants et alerte (santé « Degraded », écran).
- **Rapprochement** : chaque mois, factures et versements du canal contre réservations et commissions (import du relevé : `TODO(spec)`). Les commissions alimentent les indicateurs (port `IChannelCommissionSource` du module Reporting).

## 4. Banc de contrat

Une suite de scénarios que tout adaptateur doit passer : création confirmée puis rejouée sans doublon, modification de dates (contrôle de disponibilité), annulation, consultation, disponibilité sous un arrêt de vente et sous un quota, catégorie ou lieu non mappé (refus clair), dernière voiture demandée par deux canaux en même temps (une seule réussit), temps de réponse de la disponibilité. Elle tourne aujourd'hui sur le faux canal et tournera demain sur l'adaptateur Booking.com.

## 5. Découpage de la phase 5

| Lot | Contenu | Dépend de |
|---|---|---|
| 5A Canaux | Modèle, correspondances, quotas et arrêts de vente (`IChannelAvailabilityPolicy`), `IChannelAdapter`, adaptateur manuel, faux canal, point d'entrée, journal, idempotence, envois, resynchronisation, rejeu, alertes, commissions, écrans, banc de contrat | phase 1 |
