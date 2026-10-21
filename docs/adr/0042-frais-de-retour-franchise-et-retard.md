# ADR-0042 — Frais de retour : proposition, plafond de franchise et tarification du retard

- **Statut** : Acceptée
- **Date** : 2026-10-21
- **Source** : [spécifications](../SPEC.md) §6 (« Retour ») et §7 (moteur de prix), [prompt](../PROMPT.md) §6 (invariants 5 et 8) et §16 (aucun prix hors du moteur), [ADR-0031](0031-devis-figes-versions-et-prolongation.md), [ADR-0034](0034-paiements-et-cautions.md), [ADR-0041](0041-depart-retour-etat-des-lieux-et-contrat.md)

## Contexte

Au retour, la SPEC demande des frais calculés — kilomètres, carburant, retard au-delà de la tolérance, nettoyage, dommages **plafonnés à la franchise** —, puis la facture finale et la caution restituée ou retenue avec justificatif. Ces frais ne sont pas une modification de la location : le devis figé et ses versions ([ADR-0031](0031-devis-figes-versions-et-prolongation.md)) décrivent ce qui a été vendu, pas ce qui s'est passé pendant la location. Le prix d'un jour de retard ne peut venir que du moteur de prix. L'agent doit pouvoir corriger une proposition (geste commercial, carburant déjà refait) sans perdre la trace de ce qui était proposé, et la comptabilité doit distinguer la location, la contribution journalière et les réparations.

## Décision

### Lignes de frais

- **`ReturnCharge`** (schéma `bookings`, table temporelle) : lignes ordonnées de l'état des lieux de retour — type (kilomètres supplémentaires, carburant, retour tardif, contribution des jours de retard, nettoyage, réparation, plafond de franchise, autre), quantité, prix unitaire TTC, HT, TVA, TTC, taux de TVA, arguments de libellé (chaînes invariantes), description, motif, dommage visé, **montant proposé** par la plateforme (nul pour une ligne ajoutée par l'agent), saisi par et le.
- Elles **ne sont pas des versions du devis** : elles s'ajoutent au solde du dossier et deviendront des lignes de la facture finale (phase 3, par `IHandoverQueries.GetReturnChargesAsync`, libellé `ReturnCharge.Label.{type}` mis en forme avec les arguments).

### Proposition

Le domaine propose (`ReturnCharges.Propose`) avec les paramètres de l'agence, tous TTC (`Bookings/Contracts/HandoverSettings`, libellés dans l'administration des paramètres) :

| Frais | Calcul | Paramètre (défaut) |
|---|---|---|
| Kilomètres supplémentaires | (retour − départ) − inclus × jours facturés | `bookings.mileage_included_per_day_km` (0 = illimité), `bookings.extra_km_price` (0,300 par km) |
| Carburant | huitièmes manquants par rapport au départ (plein à plein) | `bookings.refuel_fee_per_eighth` (15,000) |
| Nettoyage | case « nettoyage nécessaire » de l'état des lieux | `bookings.cleaning_fee` (30,000) |
| Retour tardif | moteur de prix, méthode delta (ci-dessous) | `pricing.late_return_tolerance` (59 min) |
| Contribution des jours de retard | contribution journalière HT × jours, plus TVA | `billing.daily_contribution_excl_vat` |

**Retard.** Les jours de retard se comptent par tranches de 24 heures après le retour prévu, une tranche entamée au-delà de la tolérance comptant pour un jour (`RentalDays.Late`). Leur prix vient du **moteur de prix**, jamais d'un calcul local : `devis(prise → retour effectif, maintenant) − devis(prise → retour prévu, maintenant)`, avec les mêmes entrées que la location (catégorie, canal, lieux, options, code promo). La différence suit donc la grille du moment, ses saisons et ses paliers. La contribution journalière des jours ajoutés est isolée sur sa ligne pour la comptabilité ; la ligne « retour tardif » porte le reste de la différence. Si le moteur ne peut pas tarifer la période (plus de grille publiée, par exemple), l'écran l'indique et l'agent saisit un frais « autre » motivé.

### Saisie par l'agent

- Changer ou offrir une ligne proposée et ajouter des lignes est une décision d'argent : `payments.manage` (sans ce droit, la proposition s'applique telle quelle). `ReturnCharges.Finalize` applique les règles : la quantité d'une ligne proposée est fixe, un prix changé exige un motif (3 caractères au moins), une réparation vise un nouveau dommage de l'état des lieux (ou porte une description), un frais « autre » exige une description et un motif, une ligne d'un type calculé que la plateforme n'a pas proposée exige un motif ; le plafond et la contribution ne se saisissent pas.
- L'écran calcule les lignes finales en direct avec le même code que le serveur. Il renvoie le total de la proposition qu'il a affichée : si la proposition a changé entre-temps (un autre relevé, une autre heure), la fin du retour est refusée (`handover.charges.proposal_changed`) et l'écran recharge la proposition.

### Plafond de franchise

- **La franchise est la caution du devis figé** : caution de la catégorie, ou caution réduite quand le rachat de franchise a été souscrit (`Quote.Deposit`, `IncludesFranchiseWaiver`) — le chiffre contractuel du dossier, pas celui de la catégorie au jour du retour.
- Les réparations, ensemble, ne dépassent jamais la franchise : une ligne négative « plafond de franchise » ramène leur total au montant de la franchise. Ce qui dépasse relève d'un sinistre (assureur, suivi des sinistres d'une phase ultérieure), pas des frais de retour.

### TVA

- Les montants saisis sont TTC ; HT = TTC / (1 + taux) arrondi au millime, TVA = TTC − HT, au taux de l'agence (`billing.vat_rate`). Le traitement fiscal exact des réparations et des retenues (TVA ou indemnité hors champ) est à confirmer avec le comptable (`TODO(spec)`) ; il ne changerait que le taux appliqué à ces lignes.

### Solde, caution et clôture

- **Solde = total du devis courant + frais − net payé − cautions retenues.** Une retenue sur caution règle des frais ; elle n'est jamais un revenu par elle-même (invariant 8) : l'écran propose de retenir `min(solde, caution détenue)` avec un justificatif, le reste étant restitué, ou de restituer la caution quand rien n'est dû ; un trop-perçu se rembourse par le panneau des paiements.
- **Révision avant la clôture** : tant que le dossier est « Restituée », les frais peuvent être révisés (devis du carrossier arrivé ensuite) sur la proposition du même instant de retour ; les lignes sont remplacées, l'historique reste dans la table temporelle et l'événement `ReturnChargesRecorded` signale la révision.
- **Clôture** quand le solde est nul et qu'aucune caution n'est détenue, contrôlé avant et dans la transaction de clôture (`handover.close.balance_due`, `handover.close.deposit_held`).

## Conséquences

- La facture finale de la phase 3 reprend le devis courant et ces lignes (plafond compris) ; la Billing n'a pas à recalculer de frais.
- Le prix du retard suit la grille du jour du retour : il peut différer du prix journalier de la réservation (saison, paliers), ce qui est voulu — les jours ajoutés sont vendus au tarif du moment, comme une prolongation.
- Un retour anticipé ne crée aucun avoir : la différence négative du moteur est ramenée à zéro ; une politique de remboursement des jours non utilisés reste à définir (`TODO(spec)`).
- Les paramètres de frais se règlent par agence sans déploiement ; le kilométrage illimité par défaut ne propose aucun frais de kilomètres.
- Les réparations portent un montant saisi (devis, barème interne) : aucune grille de réparation n'est inventée.

## Alternatives écartées

- **Frais en nouvelle version du devis (avenant)** : la version du devis décrit la vente ; y mêler les incidents du retour brouillerait la chronologie, relancerait la tarification de toute la location et mélangerait location et indemnités en comptabilité.
- **Retard au prix journalier multiplié par les jours** : calcul de prix hors du moteur (interdit), qui ignore saisons, paliers et options.
- **Retard au prix journalier du devis figé** : ne reflète pas la grille des jours effectivement ajoutés et diverge de la prolongation, qui valorise au tarif du moment.
- **Franchise lue sur la catégorie au jour du retour** : la caution de la catégorie a pu changer depuis la réservation ; seul le devis figé fait foi.
- **Réparations sans plafond, vérifiées à la main** : contraire à la SPEC, et source d'erreurs au comptoir.
- **Retenue de caution comptée en chiffre d'affaires** : contraire à l'invariant 8 ; la retenue solde des frais qui, eux, seront facturés.
