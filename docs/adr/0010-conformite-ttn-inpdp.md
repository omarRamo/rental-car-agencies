# ADR-0010 — Conformité intégrée : facture électronique TTN, contribution journalière, INPDP

- **Statut** : Acceptée (paramètres fiscaux à valider par l'expert-comptable, procédures INPDP par un avocat)
- **Date** : 2026-10-20
- **Source** : [spécifications](../SPEC.md), sections 7, 8 et 14

## Contexte

Trois obligations sont en vigueur en 2026 :

- **Facture électronique** (art. 53 de la loi de finances 2026) via Tunisie TradeNet, au format XML TEIF signé ; une facture papier à la place coûte 100 à 500 TND d'amende par facture.
- **Contribution de 2 TND par jour et par véhicule loué**, due depuis le 1er janvier 2026, à la charge du client, incluse dans la base de TVA.
- **Loi organique 2004-63** : déclaration du traitement à l'INPDP, et autorisation préalable pour tout transfert de données hors de Tunisie (art. 52). Un transfert non autorisé est puni d'un an de prison et de 5 000 TND d'amende (art. 90).

## Décision

- Facturation : numérotation sans trou par exercice, mentions obligatoires, facture émise immuable (correction par avoir), génération du XML TEIF, signature, transmission à TTN, conservation de la référence retournée, et mode « papier transitoire » activable tant que l'adhésion n'est pas finalisée.
- Taxes paramétrables (TVA 19 % à confirmer, contribution journalière, droit de timbre), sur comptes dédiés en pré-comptabilité.
- Données personnelles : autorisation INPDP obtenue **avant** la production sur Azure France ; purge automatique des images de pièces après une durée paramétrable ; droits d'accès, de rectification et d'opposition ; conception compatible RGPD pour la clientèle européenne.

## Conséquences

- L'ouverture au public (fin du lot 2) attend l'autorisation INPDP ; les factures électroniques (fin du lot 3) attendent l'adhésion TTN.
- La procédure de purge (PV d'huissier, art. 45) et le statut du contrat de location comme facture restent à trancher (voir [points ouverts](../points-ouverts.md)).
