# Points ouverts

Suivi vivant des décisions, démarches et vérifications qui conditionnent le projet. La référence figée reste [SPEC.md](SPEC.md) (v1 du 20/10/2026) : quand un point est tranché, on le coche ici avec la date et la décision, et on met à jour l'ADR ou la spec concernée.

## Démarches de l'agence à lancer dès la semaine 1

Leurs délais ne dépendent pas du développement ; un retard décale le point de passage qu'elles débloquent.

| Démarche | Débloque | Statut |
|---|---|---|
| Dossier INPDP : déclaration du traitement et autorisation de transfert hors de Tunisie (avec un avocat) | Ouverture au public (fin du lot 2) | ⬜ À lancer |
| Contrat Clic to Pay avec pré-autorisation (STB / SMT) | Ouverture au public (fin du lot 2) | ⬜ À lancer |
| Adhésion à la facturation électronique TTN et certificat de signature | Factures électroniques (fin du lot 3) | ⬜ À lancer |
| Vérification de l'entreprise chez Meta et numéro WhatsApp dédié | Bot WhatsApp en ligne (fin du lot 4) | ⬜ À lancer |
| Candidature fournisseur Booking.com, puis contrat et cahier des charges technique | Vente sur Booking.com (fin du lot 5) | ⬜ À lancer |

## Décisions à obtenir de l'agence avant le lot 1

- [ ] Agences, lieux de remise (aéroports, hôtels, livraison) et horaires d'ouverture
- [ ] Vente par catégorie ou par modèle ; liste des 30 véhicules et de leurs catégories
- [ ] Grilles initiales : saisons, paliers, options, cautions, kilométrage, carburant
- [ ] Politique de garantie (acompte ou totalité, par saison) et politique d'annulation
- [ ] Âge minimum, ancienneté du permis, cas du permis international
- [ ] Langues au lancement (arabe dès le départ ?), nom de domaine, logo et charte
- [ ] Données à reprendre : clients, réservations futures, fichiers existants
- [ ] Outil actuel remplacé et date de bascule visée
- [ ] Numéro WhatsApp dédié et vérification de l'entreprise chez Meta
- [ ] Clic to Pay : contrat commerçant, pré-autorisation activable, durée de validité, commission
- [ ] Booking.com : candidature fournisseur, connecteur maison ou intermédiaire
- [ ] Expert-comptable : logiciel et format d'export, TVA, timbre, contrat tenant lieu de facture, adhésion TTN
- [ ] INPDP : déclaration du traitement et autorisation de transfert, avec un avocat
- [ ] Cibles des objectifs mesurables (section 2 de la spec), proposées mais à valider

## Hypothèses à confirmer pendant les lots

| Sujet | À confirmer | Avec qui | Lot |
|---|---|---|---|
| TVA | Taux normal de 19 % sur la location | Expert-comptable | 1 |
| Droit de timbre | Barème en vigueur sur facture | Expert-comptable | 3 |
| Contrat tenant lieu de facture | Le contrat de location peut-il remplacer la facture (note générale n°2/2026) ? | Expert-comptable | 3 |
| Clic to Pay — montants | Montant envoyé en millimes (× 1 000) : faire un paiement test de 1,000 TND dès la recette | Recette Clic to Pay | 2 |
| Clic to Pay — statuts | Codes de statut annulée, remboursée, refusée (seuls 0, 1, 2 sont connus) | Documentation SMT | 2 |
| Clic to Pay — pré-autorisation | Durée de vie d'une pré-autorisation ; activation du paiement en deux étapes | SMT / banque | 2 |
| Clic to Pay — cartes étrangères | Acceptation des cartes étrangères (diaspora ≈ 80 % de la demande estivale) | Banque | 2 |
| QuestPDF | Seuil de la licence gratuite | — | 1 |
| Coûts Azure | Prix réels en France Central (la source du prix Azure SQL date de 2016) ; offre gratuite Azure SQL toujours proposée pour la recette | Calculatrice Azure | 0 |
| Purge des pièces d'identité | Procédure de destruction (art. 45 : PV d'huissier, expert INPDP) | Avocat | 2 |
| Contrat et conditions générales | Franchise, caution, carburant, retard, sortie du territoire, amendes | Avocat | 1 |
| Matricule fiscal | Format exact du matricule fiscal des sociétés (validé de façon souple pour l'instant) | Expert-comptable | 1 |
| Pièce d'un conducteur tunisien | Un Tunisien doit présenter une CIN délivrée en Tunisie ; un passeport ne la remplace pas (lecture stricte de la spec, règle d'éligibilité actuelle) | Agence | 1 |
| Permis international | Le pays de délivrance du permis international n'est pas comparé à celui du permis national | Agence | 1 |
| Droit d'accès et liste noire | L'export des données d'une personne inclut-il son inscription sur la liste noire ? (option désactivée par défaut) | Avocat | 1 |
| Historique après anonymisation | Les tables temporelles gardent les anciennes valeurs ; procédure T-SQL de purge de l'historique fournie dans l'ADR-0027, à exécuter par un administrateur | Avocat | 1 |

## Incohérences relevées dans la spec v1

À trancher puis corriger dans une v2 de la spec.

- [ ] **Devise du coût d'infrastructure** : « 20 à 35 USD » (résumé, section 12) contre « ≈ 20 à 35 € » (tableau des objectifs, section 2).
- [ ] **Prix du marché** : le texte dit « 120 à 150 TND par jour » (section 7), la source Tuniscope citée s'intitule « des prix entre 120 et 180 dinars par jour ».
- [ ] **Statut manquant** : le plan de repli Clic to Pay (section 9) introduit « Validée, en attente de paiement », absent du cycle de vie (section 4). À ajouter au cycle, ou à modéliser comme « Confirmée » avec paiement en attente.
- [ ] **Fin d'une option datée** : elle « expire seule », mais le cycle de vie ne dit pas vers quel statut (« Expirée » ?) ni si la capacité est libérée de la même façon.
- [ ] **Décompte des améliorations** (section 16) : « Quatorze ajouts […] ; les cinq derniers points sont des évolutions possibles », alors que le tableau compte déjà 14 lignes et que les 5 évolutions sont listées à part.

## Écarts entre PROMPT.md et SPEC.md

[PROMPT.md](PROMPT.md) demande de signaler ces points avant de coder. L'exécution étant autonome, chacun est tranché par défaut, de façon réversible ; la décision prise est indiquée.

| # | Écart ou question | Décision par défaut |
|---|---|---|
| 1 | La spec parle de « tarifs de chaque véhicule », le prompt de tarifs par catégorie. | Grille par catégorie ; prix par véhicule seulement pour l'option « modèle garanti » (règle 2 du moteur de prix de la spec). |
| 2 | Statut « Validée, en attente de paiement » (plan de repli Clic to Pay) absent du cycle de vie. | Statut `AwaitingPayment` ajouté : actif pour la capacité, il expire comme une demande si le lien `register.do` n'est pas payé dans les 24 h. |
| 3 | Statut d'arrivée d'une option datée qui expire. | `Expired`, avec libération de la capacité, comme une demande web. |
| 4 | Plafond du siège enfant (50,000 TND) présent dans le prompt, absent de la spec. | Repris : plafond paramétrable par option. |
| 5 | Le prompt numérote « ADR 0004 = verrou de disponibilité » ; dans le dépôt, l'ADR 0004 traite de l'hébergement et le verrou est l'ADR 0006. | Numérotation existante conservée ; les nouvelles ADR démarrent à 0011. |
| 6 | Cache de disponibilité : < 1 s dans la spec, < 100 ms dans le prompt. | Cible du prompt (< 100 ms), plus stricte. |
| 7 | « La recette est déployée » (fin de phase 0) suppose un abonnement Azure et des identifiants. | Bicep et `deploy.yml` livrés et validés localement ; le déploiement réel attend les identifiants Azure (OIDC) dans les secrets GitHub. |
| 8 | Double authentification obligatoire : comment se connectent les tests E2E ? | Utilisateurs de démonstration avec clé TOTP connue, uniquement en développement et en test. |
| 9 | Valeur du droit de timbre. | Paramètre à 0,000 TND par défaut, `TODO(spec)` : valeur à fournir par l'expert-comptable. |
| 10 | Licence QuestPDF Community (seuil de chiffre d'affaires). | Utilisée en Community ; à confirmer avec le chiffre d'affaires de l'agence. |
| 11 | Le prompt prévoit des séquences SQL pour les numéros `LOC`, `FAC`, `AV` ; la spec exige des factures « numérotées sans trou par exercice », ce qu'une séquence ne garantit pas (valeurs perdues à l'annulation et au redémarrage). | Table de compteurs incrémentée dans la transaction de l'appelant ([ADR-0016](adr/0016-numerotation-continue-table-de-compteurs.md)). |
