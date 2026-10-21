# Conception — Phase 3 (finance et pilotage)

Contrat commun aux agents de la phase 3. Il précise [SPEC.md](../SPEC.md) §8 (« Finance et pilotage ») et [PROMPT.md](../PROMPT.md) §6 (invariants 6 et 8), §9.4 et §15 (phase 3). En cas de doute, ces documents priment et l'écart va dans [points-ouverts.md](../points-ouverts.md).

**Fini quand** (PROMPT §15) : un mois simulé produit des journaux exportés qui équilibrent (débits = crédits) et des indicateurs cohérents avec les dossiers.

## 1. Facturation (module Billing)

- **Facture** (`Invoice`) : numéro `FAC-AAAA-NNNNNN` sans trou par exercice (`IDocumentNumberGenerator`, dans l'unité de travail), date d'émission (Tunis), vendeur (identité légale de l'agence : raison sociale, matricule fiscal, adresse, registre — à compléter dans le référentiel), client (le payeur du dossier : particulier, ou société avec matricule fiscal et adresse de facturation), dossier de référence, lignes (libellé, quantité, prix unitaire, HT, TVA, TTC, taux, nature comptable), totaux par taux, **contribution journalière** sur sa propre ligne (2,000 HT par jour et par véhicule, soumise à TVA), **droit de timbre** ajouté au total, hors TVA (`billing.stamp_duty`, `TODO(spec)` : valeur de l'expert-comptable), mentions obligatoires, état de facture électronique.
- **Émise = immuable** (invariant 6) : aucune modification après émission ; toute correction passe par un **avoir** (`CreditNote`, `AV-AAAA-NNNNNN`, motif obligatoire, total ou partiel, lignes négatives rattachées à la facture).
- **Moments de facturation** : à la clôture d'un dossier (devis en vigueur + frais de retour de 1F-b ; une facture par dossier, les paiements reçus étant rapprochés) ; frais d'annulation ; retenue sur caution (dommages) ; refacturation d'une amende. Un dossier Booking.com prépayé est facturé au client du canal selon le contrat (`TODO(spec)`), la commission étant une charge.
- **Facture électronique** : `IEInvoiceProvider` avec deux modes. `PaperTransitional` (par défaut tant que l'adhésion TTN n'est pas finalisée) produit le PDF avec la mention correspondante. `Ttn` (XML TEIF signé, transmission, référence unique retournée) : interface, faux et tests ignorés tant que le schéma officiel n'est pas fourni (`TODO(spec)`). Le contrat de location peut-il tenir lieu de facture ? `TODO(spec)` (expert-comptable).
- **PDF** (QuestPDF) avec toutes les mentions, la référence TTN quand elle existe, stocké dans le conteneur privé `invoices`.

## 2. Amendes et sinistres

- **Amende** (`Fine`) : PV saisi (numéro, date et heure, lieu, immatriculation, montant, organisme) ; le système retrouve le dossier et le conducteur qui avaient le véhicule à cet instant (dossier `InProgress` ou `Returned` couvrant l'instant), produit la **fiche de désignation** (PDF) et, si l'agence le décide, une refacturation (facture, frais de gestion paramétrables).
- **Sinistre** (`Claim`) : constat, photos, assureur et numéro de dossier, franchise, coûts, remboursements, statut (ouvert, chez l'assureur, clos), rattaché au dossier et au véhicule ; ses coûts entrent dans la rentabilité du véhicule.

## 3. Dépenses, rapprochements et pré-comptabilité

- **Dépenses** (module Cash) : carburant, entretien, réparations, assurance, vignette, loyers de leasing, commissions (Booking.com, Clic to Pay), loyer, charges ; montant HT/TVA/TTC, fournisseur, date, moyen de paiement (une dépense en espèces devient un mouvement de caisse), justificatif photographié (conteneur privé), rattachement au véhicule quand c'est possible.
- **Rapprochements** : paiements Clic to Pay enregistrés contre statuts de la passerelle (import du relevé marchand : `TODO(spec)`) ; versements Booking.com contre réservations et commissions (phase 5) ; caisse contre encaissements (sessions clôturées) ; écarts listés et justifiés.
- **Pré-comptabilité** : journaux des ventes, des achats, de banque et de caisse ; plan de comptes paramétrable (système comptable tunisien) avec une correspondance par type d'opération (prestations, TVA collectée, contribution journalière, droit de timbre, clients, cautions reçues (dette), caisse, banque, Clic to Pay, commissions…) ; chaque écriture équilibrée ; **export CSV et Excel** dans un format générique (date, journal, compte, pièce, libellé, débit, crédit, lettrage), le format définitif étant `TODO(spec)` (logiciel de l'expert-comptable) ; **clôture mensuelle** qui verrouille la période (plus de facture, paiement, dépense ou mouvement daté dans une période close).
- **Cautions** : toujours au passif (dette envers le client) ; une retenue devient un produit seulement par la facture de dommages (invariant 8).

## 4. Indicateurs (module Reporting)

- **Instantané quotidien** : une ligne par véhicule et par jour (disponible ou non, loué ou non, dossier, chiffre d'affaires de location du jour réparti uniformément sur les jours loués du devis en vigueur, hors contribution et hors caution, canal, coûts directs du jour (dépenses rattachées), amortissement du jour), calculé chaque nuit pour la veille, recalculable sur une période.
- **Indicateurs** (SPEC §8) : revenu par véhicule disponible et par jour, taux d'utilisation, prix moyen par jour loué, marge par véhicule (revenus − coûts directs − amortissement), chiffre d'affaires et marge par canal (après commissions), carnet à 30, 60 et 90 jours (jours vendus par catégorie), conversion du site et du bot (recherches → demandes → confirmées, depuis les événements d'entonnoir), délai médian de validation et taux de refus, annulations et non-présentés par canal, encaissé, facturé, cautions ouvertes, impayés.
- Graphiques dans le back office (accessibles : tableau de données équivalent), exports Excel (ClosedXML), vues SQL prêtes pour Power BI plus tard.

## 5. Découpage de la phase 3

| Lot | Contenu | Dépend de |
|---|---|---|
| 3A Facturation | Factures, avoirs, numérotation, taxes, droit de timbre, `IEInvoiceProvider` (papier transitoire), PDF, facturation à la clôture et des frais, amendes et sinistres, écrans | 1F-b (frais de retour, clôture) |
| 3B Dépenses et comptabilité | Dépenses, rapprochements, plan de comptes, journaux, exports, clôture mensuelle, simulation d'un mois équilibrée | 3A |
| 3C Indicateurs | Instantané quotidien, indicateurs, graphiques, exports Excel | phase 1 (et 3B pour les dépenses, via un contrat) |
