# ADR-0035 — Sessions de caisse : une par agent et par jour, espèces de recette et cautions comptées à part, verrouillage définitif

- **Statut** : Acceptée
- **Date** : 2026-10-20
- **Source** : [spécifications](../SPEC.md) §3 (clôture de caisse : l'agent sa caisse, gérant et comptable gèrent), §8 (« Caisse », « Rapprochements »), §13 (`CashSession`, `CashMovement`) ; [prompt](../PROMPT.md) §5 (agrégat `CashSession`) et invariant 8 ; [ADR-0034](0034-paiements-et-cautions.md)

## Contexte

SPEC §8 : « Une session par agent et par jour : fonds de caisse, entrées et sorties typées, comptage de clôture, écart justifié, verrouillage. Les cautions en espèces sont suivies à part. » Les espèces reçues au départ (solde, caution) et rendues au retour doivent passer par un tiroir identifié, pour que chaque agent réponde de sa caisse et que la phase 3 rapproche la caisse des encaissements.

## Décision

- **Agrégat `CashSession`** (module Caisse) : agence, caissier (utilisateur) et son nom affiché, jour d'exploitation (date de Tunis de l'ouverture), fonds de caisse, statut `Open` → `Closed` → `Locked`, mouvements `CashMovement` numérotés et ajoutés seulement, espèces attendues de chaque poche tenues sur la racine, comptage et écarts.
- **Unicité** : une session par caissier, agence et jour, et au plus une session ouverte par caissier. `CashSessionRules` les vérifie ; deux index uniques (dont un filtré sur les sessions ouvertes) les garantissent : deux ouvertures simultanées n'en créent qu'une. Une session clôturée ne se rouvre pas le même jour. Une session reste ouverte après minuit (retours tardifs) : son jour est celui de l'ouverture, et l'écran signale une session ouverte depuis un jour précédent.
- **Deux poches** : les espèces de recette (fonds de caisse, paiements reçus, remboursements payés, entrées et sorties manuelles, part retenue des cautions) et les cautions en espèces (reçues, rendues, retenues, reprises, remises), chacune avec ses espèces attendues, son comptage et son écart. Chaque mouvement porte son effet signé sur chaque poche ; l'écran affiche les soldes attendus après chaque ligne.
- **Mouvements typés** : paiement reçu, remboursement payé, caution reçue, caution rendue, caution retenue (cautions −, recette +), caution reprise, caution remise, entrée et sortie manuelles avec motif obligatoire, ajustement signé sur une poche avec motif obligatoire. Un paiement n'entre qu'une fois par type ; une caution n'est reçue, réglée et remise qu'une fois.
- **Jamais de poche négative** : une sortie (remboursement, sortie manuelle, ajustement, restitution) qui dépasse les espèces attendues est refusée — l'agent ne paie pas des espèces que le tiroir n'a pas. Seule la remise automatique d'une caution, qui constate un fait, y échappe.
- **Caution réglée ailleurs que dans la session qui l'a reçue** : elle est d'abord reprise dans le tiroir qui la règle (depuis le coffre quand la session d'origine est close) ; si la session d'origine est encore ouverte, elle en est remise dans la même transaction. Chaque tiroir compte ainsi les enveloppes qu'il détient, et une session close ne change jamais.
- **Clôture** par le caissier (ou un détenteur de `cash.manage`) avec les espèces de recette et les cautions comptées ; écart = compté − attendu, par poche ; justification obligatoire dès qu'un écart n'est pas nul.
- **Verrouillage** par le gérant ou le comptable (`cash.manage`) d'une session clôturée : plus aucune modification. Une erreur constatée ensuite se corrige par un **ajustement dans une session ouverte ultérieure**, qui désigne la session corrigée : la session verrouillée reste telle qu'elle a été comptée.
- **Espèces des paiements** : le module Paiements les enregistre par `ICashDrawer` (contrat du module Caisse), dans la même transaction que le paiement ou la caution, toujours dans la session ouverte de l'utilisateur qui agit, de la même agence, avec la permission `cash.own`.
- **Droits** : `cash.own` (agent, comptable, gérant) donne sa propre session ; `cash.manage` (gérant, comptable) donne toutes les sessions, les ajustements et le verrouillage. Un agent qui vise la session d'un autre est refusé.
- **Concurrence** : chaque mouvement met à jour la racine, dont la `rowversion` protège toute la session (deux encaissements simultanés dans le même tiroir : l'un échoue proprement, aucun mouvement n'est perdu).
- **Persistance** : schéma `cash`, table temporelle `CashSessions`, table `CashMovements` (index unique session et numéro), clés étrangères vers les paiements et les cautions, `decimal(18,3)`.
- **Données de démonstration** (ordre 800) : la veille, une session de l'agent de démonstration clôturée avec un écart justifié et verrouillée par le gérant ; le jour même, une session ouverte ; mouvements manuels seulement, aucun dossier inventé.

## Conséquences

- `ICashQueries.GetSessionTotalsAsync` donne à la phase 3 les totaux par type de mouvement de chaque session, pour le rapprochement « caisse contre encaissements ».
- Les dépenses payées en espèces (phase 3) deviendront un type de mouvement de la poche de recette.
- Une session clôturée par erreur ne se rouvre pas : les espèces suivantes passent par la session d'un autre utilisateur ou attendent le lendemain (`TODO` éventuel : réouverture par le gérant avant verrouillage, motivée).
- La session de démonstration ouverte est celle du premier démarrage : les données de démonstration sont idempotentes, pas rafraîchies chaque jour.

## Alternatives écartées

- **Soldes de cautions calculés à la volée** d'après les cautions encore détenues : une session close changerait après coup.
- **Une seule poche** mêlant cautions et recette : contraire à SPEC §8 et à l'invariant 8.
- **Plusieurs sessions par jour et par agent** : contraire à la spécification, et l'écart du jour ne serait plus lisible.
- **Correction d'une session verrouillée** : ôterait sa valeur probante au verrouillage.
