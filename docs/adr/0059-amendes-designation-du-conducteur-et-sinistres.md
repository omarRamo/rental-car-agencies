# ADR-0059 — Amendes : rapprochement, désignation du conducteur et refacturation ; sinistres

- **Statut** : Acceptée (modèle officiel de désignation, TVA des refacturations et traitement comptable des sinistres à confirmer)
- **Date** : 2026-10-21
- **Source** : [spécifications](../SPEC.md) §8 (« Amendes et sinistres ») et §14, [conception phase 3](../conception/phase-3-finance.md) §2, [ADR-0026](0026-pieces-identite-stockage-et-acces-journalise.md), [ADR-0042](0042-frais-de-retour-franchise-et-retard.md), [ADR-0046](0046-indicateurs-de-pilotage-et-sources.md), [ADR-0057](0057-factures-avoirs-numerotation-et-moments-de-facturation.md)

## Contexte

L'agence reçoit des avis de contravention pour ses véhicules, parfois des semaines après la location : elle doit retrouver qui avait le véhicule à l'instant de l'infraction, désigner ce conducteur à l'autorité avec ses pièces d'identité — des données personnelles sensibles — et, si elle le décide, refacturer l'amende au client. Les accidents, eux, ouvrent un sinistre suivi avec l'assureur : constat, photos, franchise, coûts et remboursements, dont le coût net pèse sur la rentabilité du véhicule. Les réparations au-delà de la franchise ne sont pas des frais de retour (ADR-0042) : elles relèvent du sinistre.

## Décision

### Amendes (`Fine`, schéma `billing`, permission `fines.manage`)

- **Saisie** : numéro de l'avis, autorité, date et heure de l'infraction (heure de Tunis), lieu, immatriculation telle qu'imprimée, montant, notes. Un avis est unique par agence, autorité et numéro (`billing.fine.duplicate`, index unique). Une infraction future est refusée.
- **Rapprochement** (`FineMatching`) : la clé d'immatriculation (lettres et chiffres en majuscules, `123 TU 4567` → `123TU4567`) retrouve le véhicule de l'agence ; les locations qui l'avaient **effectivement** à cet instant (prise en charge réelle, retour réel ou en cours, changements de véhicule compris) donnent le dossier et le conducteur principal. Résultat : trouvé, plusieurs possibles, aucune location, immatriculation inconnue, ou attribué par un agent — qui choisit alors la location et l'un de ses conducteurs, revérifiés côté serveur.
- **Fiche de désignation** (PDF, QuestPDF) : agence, autorité, avis, véhicule, contrat et période, identité, date de naissance, nationalité, adresse et pièces (CIN ou passeport, permis, permis international) du conducteur. Elle imprime les pièces **en clair** : il faut aussi `customers.documents.read` ; elle est **produite à chaque demande, jamais stockée**, servie en `no-store`, et chaque production écrit une ligne par pièce dans le journal des accès aux documents (`designation_form`, motif `fine:{numéro}`) et un événement d'audit `billing.fine_designation_generated`. L'envoi à l'autorité est ensuite enregistré (« conducteur désigné »). `TODO(spec)` : modèle officiel exigé par chaque autorité (police, garde nationale, municipalités) et délai de désignation.
- **Refacturation** (optionnelle, `invoices.issue` en plus) : une facture `FineReinvoicing` au payeur du dossier, avec l'amende à son montant et les frais de gestion de l'agence (`billing.fine_management_fee`, TTC, 0 par défaut) — une fois par amende (verrou `billing:fine:{id}`, index unique). `TODO(spec)` : l'amende est refacturée sans TVA (débours) et les frais de gestion au taux de l'agence, à confirmer avec l'expert-comptable.
- **Statuts** : enregistrée → conducteur désigné → refacturée → payée → clôturée (avec une note) ; une amende sans location (véhicule non loué, immatriculation inconnue) se clôt avec sa note.

### Sinistres (`Claim`, schéma `billing`, permission `claims.manage`)

- Ouvert pour un **véhicule de l'agence** et, s'il est connu, le dossier pendant lequel l'accident a eu lieu (par son numéro) : date et heure, lieu, description, assureur et son numéro de sinistre, **franchise** du contrat.
- **Pièces** (constat amiable, photos, autres) dans le conteneur privé **`inspections`** (`claims/{sinistre}/{fichier}`), type réel lu dans les premiers octets (JPEG, PNG, WebP, PDF), 10 Mo au plus, nom d'origine gardé pour l'affichage seulement ; servies au back office seulement (`claims.manage`, `ETag`, `private, no-cache`). Un fichier retiré est effacé après la validation (outbox).
- **Coûts** (réparation, remorquage, expertise…) et **remboursements** (assureur, tiers, franchise payée par le client…) datés, avec une référence ; **coût net = coûts − remboursements**. Statuts : ouvert ⇄ chez l'assureur → clos (plus aucune modification).
- **Source Reporting `claims`** (`IVehicleCostsSource`) : chaque coût à sa date, chaque remboursement en coût négatif à sa date, montants payés (sans ventilation de TVA). Une réparation suivie dans un sinistre ne se saisit pas aussi en coût du véhicule dans la flotte, pour ne rien compter deux fois.

## Conséquences

- Une amende reçue après la location trouve son conducteur sans recherche manuelle dans les dossiers ; les cas ambigus restent dans la liste « à attribuer ».
- Les données personnelles imprimées pour l'autorité ne sont conservées nulle part dans la plateforme et chaque production est traçable, comme la consultation des pièces (ADR-0026).
- Le coût d'un sinistre et ses remboursements entrent dans la marge du véhicule des indicateurs dès leur saisie.
- `TODO(spec)` : comptabilisation des coûts et remboursements de sinistres (lot 3B) ; refacturation de la franchise au client au-delà de la caution retenue.

## Alternatives écartées

- **Rapprochement sur la période prévue du dossier** : un retour anticipé ou tardif désignerait le mauvais conducteur ; seules les prises en charge et restitutions réelles font foi.
- **Fiche de désignation stockée avec l'amende** : une copie de plus des pièces d'identité, sans besoin ; elle se régénère à l'identique.
- **Refacturation automatique de chaque amende** : décision commerciale de l'agence, au cas par cas.
- **Sinistre comme frais de retour** : la franchise plafonne les frais de retour ; au-delà, le suivi avec l'assureur et les remboursements n'ont pas leur place dans le solde du dossier.
