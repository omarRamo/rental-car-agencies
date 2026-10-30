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
| Départ et retour | Texte définitif des conditions générales (`CG-LOC-2026-1`) ; valeur probante de la signature dessinée sur tablette ; durée de conservation des états des lieux, photos et contrats ; remboursement d'un retour anticipé ; TVA des réparations et des retenues sur caution ; règles de prépaiement et de caution des dossiers Booking.com | Avocat / expert-comptable / agence | 1 |
| Politique de garantie du tunnel | Part du total TTC pré-autorisée à la demande web (`bookings.online_guarantee_ratio`, 1 = totalité par défaut) : acompte ou totalité, par saison ou par canal (SPEC §8) | Agence | 2 |
| Avenant d'un dossier Booking.com | Une prolongation ou un surclassement fait au comptoir sur un dossier Booking.com applique les restrictions du canal (arrêt de vente, réserve), alors que la saisie manuelle et le départ anticipé les ignorent : faut-il les appliquer au comptoir ? (ADR-0025) | Agence | 5 |
| Tableau du jour et retours en retard | Une voiture attendue hier et rendue aujourd'hui n'apparaît pas dans les retours du jour (elle reste sur son jour prévu) : l'ajouter aux retours du jour, avec l'heure réelle ? | Agence | 1 |
| Matricule fiscal | Format exact du matricule fiscal des sociétés (validé de façon souple pour l'instant) | Expert-comptable | 1 |
| Pièce d'un conducteur tunisien | Un Tunisien doit présenter une CIN délivrée en Tunisie ; un passeport ne la remplace pas (lecture stricte de la spec, règle d'éligibilité actuelle) | Agence | 1 |
| Permis international | Le pays de délivrance du permis international n'est pas comparé à celui du permis national | Agence | 1 |
| Droit d'accès et liste noire | L'export des données d'une personne inclut-il son inscription sur la liste noire ? (option désactivée par défaut) | Avocat | 1 |
| Historique après anonymisation | Les tables temporelles gardent les anciennes valeurs ; procédure T-SQL de purge de l'historique fournie dans l'ADR-0027, à exécuter par un administrateur | Avocat | 1 |
| Cookie de mesure de l'entonnoir | Le cookie de visite anonyme (première partie, sans donnée personnelle) exige-t-il un consentement ? | Avocat | 2 |
| Origine réservée à Cloudflare | Prêt dans l'infrastructure : paramètre Bicep `restrictToCloudflare` (désactivé par défaut), à activer en production une fois `www.<domaine>` derrière Cloudflare, avec la variable GitHub `HEALTHCHECK_BASE_URL` ; il règle aussi `ReverseProxy__TrustCloudflareHeader=true` (l'en-tête `CF-Connecting-IP` n'est plus cru sans lui). Plages de Cloudflare à comparer chaque trimestre ; alternative : « Authenticated Origin Pulls » avec un certificat de zone ([README de l'infrastructure](../infra/README.md#8-cloudflare-devant-lapp-service) §8, [ADR-0062](adr/0062-origine-cloudflare-et-revue-de-securite.md)) | Agence (infra) | 2 |
| Clés Turnstile | Créer le widget Turnstile de l'agence et renseigner `Turnstile:SiteKey` et le secret `Turnstile--SecretKey` avant d'ouvrir le tunnel (sinon les soumissions sont refusées) | Agence (infra) | 2 |
| Cache Cloudflare des pages | Règle de cache Cloudflare pour le HTML public (les pages envoient `Cache-Control: public, max-age=300`) | Agence (infra) | 2 |
| Contrat Booking.com | Taux et TVA de la commission, prépayé ou payé au comptoir, formule et relevé des versements, règles de non-présentation et d'annulation, devises (seul le TND est accepté) | Agence / Booking.com | 5 |
| Codes des canaux | Codes Booking.com des catégories et des lieux, codes OpenTravel des options, assurances et frais ; politique de quotas (ex. 2 véhicules hors Booking.com en haute saison) ; qui reporte les changements sur l'extranet | Agence | 5 |
| Certificat client derrière Cloudflare | L'authentification par certificat client d'un canal suppose un accès direct à l'origine ou un en-tête de confiance (`Channels:Inbound:TrustArrClientCertHeader`) | Agence (infra) | 5 |
| Agent IA — sous-traitant et zone | Les messages des clients partent chez Anthropic (API Claude, États-Unis) ou chez Microsoft Foundry, dont les déploiements Claude sont « Global » ou « US Data Zone », **sans zone européenne** : transfert hors de Tunisie à couvrir par l'autorisation INPDP et par le contrat de sous-traitance (le modèle ne reçoit ni numéro, ni nom, ni pièce : ADR-0056) | Avocat | 4 |
| Agent IA — tarifs du modèle | Prix de `claude-haiku-5-5` : défauts des réglages 0,10 USD (entrée) et 0,50 USD (sortie) par million de jetons, cache lu à 0,1 × et écrit à 1,25 × l'entrée — à vérifier sur la page des tarifs (Anthropic ou Foundry) | Agence | 4 |
| Agent IA — coût Meta | Prix d'une réponse de service facturable selon la tarification Meta du 1er octobre 2026 (réglage `ai.meta_usd_per_message` à 0 par défaut, `TODO(spec)`) | Meta / agence | 4 |
| Agent IA — modèle et cache | Disponibilité de `claude-haiku-5-5` sur le compte (API ou Foundry), minimum de jetons du cache de prompt pour ce modèle (préfixe d'environ 3 500 jetons : sous le minimum, il n'est pas mis en cache) ; Foundry accepte-t-il les messages système en cours de conversation ? (par défaut, le contexte du tour y va dans le dernier message du client) | Anthropic / Microsoft | 4 |
| Agent IA — base de connaissances | Relire et valider les brouillons repris de la FAQ dans `/admin/whatsapp/knowledge` ; saisir les conditions que le moteur de prix ne donne pas (kilométrage, assurance, carburant, retard) : sans réponse validée, le bot passe la main | Gérant | 4 |
| Agent IA — ouverture | Lancer l'évaluation contre le vrai modèle (workflow manuel `ai-eval`, clé de l'agence) et ouvrir d'abord à des numéros pilotes (`Ai:PilotNumbers`) ; durée de conservation des relevés de coût du bot (sans texte) | Agence / avocat | 4 |
| Conservation des pièces comptables | 10 ans (factures, avoirs, contrat s'il tient lieu de facture) : sauvegarde annuelle de la base gardée 10 ans et conteneur `invoices` immuable 10 ans, politique non verrouillée ; décider ensuite de verrouiller (irréversible) les factures et les sauvegardes longues ([ADR-0061](adr/0061-sauvegardes-retention-et-immuabilite.md)) | Expert-comptable / agence | Durcissement |
| Destinataires des alertes | Adresses de `ALERT_EMAILS`, SMS (facturés) ou non, surveillance externe gratuite ou test de disponibilité payant ([ADR-0060](adr/0060-alertes-et-telemetrie-de-sante.md), [runbook](runbook.md)) ; contacts du runbook à remplir | Agence | Durcissement |
| Durée maximale de session | Le personnel se reconnecte au plus tard 12 heures après sa connexion (`Identity:Session:AbsoluteLifetime`) : compatible avec les horaires du comptoir en haute saison ? | Agence | Durcissement |
| Double authentification perdue | Réinitialisation par le gérant dans `/admin/users` (aujourd'hui : procédure SQL du runbook, hors journal d'audit) | Agence / développement | Durcissement |
| Politique de mot de passe | Retirer les règles de composition et refuser les mots de passe divulgués (service externe Have I Been Pwned, par k-anonymat) ? ([revue de sécurité](securite/revue-owasp-2026-10.md), A-03) | Agence | Durcissement |
| Analyse CodeQL | CodeQL n'analyse qu'un dépôt public ou un dépôt avec GitHub Advanced Security : vérifier la visibilité du dépôt | Agence (infra) | Durcissement |
| Facture électronique TTN | Schéma TEIF (version, correspondance des champs), signature XAdES (certificat de l'agence, prestataire, code ou HSM), transport (adresses, authentification, idempotence par numéro), référence retournée et codes d'erreur ; d'ici là l'adaptateur TTN répond « non configuré » ([ADR-0058](adr/0058-documents-de-facturation-pdf-envoi-et-facture-electronique.md)) | TTN / expert-comptable | 3 |
| Mentions des factures | Liste définitive des mentions obligatoires des factures et des avoirs, et libellé de la mention du régime papier transitoire (paramètres `billing.invoice_mentions` et `billing.einvoice_transitional_mention`) | Expert-comptable | 3 |
| Droit de timbre — avoirs et solde | Un avoir reprend-il le timbre ? Le timbre est-il encaissé au comptoir ou intégré au solde de clôture du dossier (il n'y entre pas aujourd'hui : la facture d'un dossier soldé l'affiche en reste à payer) ? | Expert-comptable | 3 |
| TVA des refacturations | Frais d'annulation ; dommages et retenues sur caution ; amende refacturée (débours sans TVA, comme aujourd'hui ?) et frais de gestion (taux de l'agence aujourd'hui) | Expert-comptable | 3 |
| Séries de numéros | Une seule série `FAC` / `AV` pour la plateforme ; une série par agence si les agences sont des sociétés distinctes | Expert-comptable | 3 |
| Facture d'un dossier Booking.com | Payeur facturé (client ou Booking.com), mentions et commission selon le contrat du canal ; seule la référence du canal est imprimée aujourd'hui | Agence / expert-comptable | 3 |
| Factures après anonymisation | Les factures et avoirs gardent l'identité de l'acheteur après l'anonymisation du client (pièces comptables) : durée de conservation et base légale | Avocat / expert-comptable | 3 |
| Fiche de désignation | Modèle officiel attendu par chaque autorité (police, garde nationale, municipalités), délai et mode d'envoi ; la plateforme produit une lettre de désignation générique ([ADR-0059](adr/0059-amendes-designation-du-conducteur-et-sinistres.md)) | Agence / avocat | 3 |
| Sinistres | Comptabilisation des coûts et des remboursements (lot 3B) ; refacturation au client de la franchise au-delà de la caution retenue | Expert-comptable / agence | 3 |
| Facture hors dossier | Pas de facture manuelle (hors dossier et hors amende) dans le lot 3A : l'agence en a-t-elle besoin ? | Agence | 3 |
| Domaines du widget Turnstile | Le serveur exige désormais que le défi ait été résolu sur l'hôte qui reçoit le formulaire : déclarer dans le widget Cloudflare exactement les noms du site (domaine, `www`, recette) **et chaque domaine propre d'une agence** ([ADR-0069](adr/0069-domaines-propres-par-agence.md)) ; une clé de test est refusée en production ([revue de sécurité](securite/revue-owasp-2026-10.md), P2-03, [ADR-0066](adr/0066-revue-de-securite-passe-2.md)) | Agence (infra) | 2 |
| Durée du lien et du code « Ma réservation » | 30 minutes aujourd'hui (`bookings.my_booking_link_validity`), l'ASVS 2.7.2 recommande 10 : passer à 15 ? Garder `bookings.my_booking_requests_per_hour × bookings.my_booking_code_attempts` sous 100 essais par heure (A-17) | Agence | 2 |
| Plafond de dépense de l'agent IA | Les plafonds sont par conversation (0,50 USD) : fixer une limite de dépense mensuelle chez le fournisseur (console Anthropic, budget Azure pour Foundry) avant d'ouvrir le bot à tous les numéros (A-22) | Agence | 4 |
| Plan de comptes par défaut | Valider ou remplacer les comptes semés « à valider par l'expert-comptable » (numéros, libellés) et les correspondances des 49 types d'opération ; un plan et une série par agence si les agences sont des sociétés distinctes ([ADR-0065](adr/0065-plan-de-comptes-journaux-exports-et-cloture.md)) | Expert-comptable | 3 |
| Format d'export comptable | Logiciel de l'expert-comptable, format d'import, codes des journaux (VT, AC, BQ, CA, OD), numérotation des pièces et lettrage attendus ; d'ici là, CSV et Excel génériques `date, journal, compte, pièce, libellé, débit, crédit, lettrage` avec total de contrôle | Expert-comptable | 3 |
| Relevé marchand Clic to Pay | Format du relevé de SMT (colonnes, références, dates de remise, frais, mode de mise à disposition) ; d'ici là, CSV générique ou saisie ligne à ligne ([ADR-0064](adr/0064-rapprochements-caisse-clic-to-pay-booking-com.md)) | SMT / banque | 3 |
| Relevé des versements Booking.com | Format du relevé et détail par réservation, devise, date de valeur ; d'ici là, versement saisi dans l'écran des canaux | Booking.com / agence | 3 |
| TVA de la commission Booking.com | La commission déduite du versement est passée hors TVA en `6226` : facture de Booking.com avec TVA, autoliquidation, retenue à la source ? | Expert-comptable | 3 |
| Comptabilisation des cautions | Cautions en `165`, garanties en `5812` (empreinte) et `5813` (chèque en portefeuille, jamais remis à l'encaissement) ; la part retenue règle le client (`411`) et s'encaisse en `532` : à valider, comme le traitement d'un chèque de caution encaissé | Expert-comptable | 3 |
| Débours et contributions | Amende refacturée en débours (`4671`, sans TVA) et frais de gestion en produit (`7583`) ; contribution journalière en `4367` (compte de l'État, pas un produit) ; écarts de caisse en `658` / `758` ; écarts de versement en attente `471` | Expert-comptable | 3 |
| TVA des dépenses | TVA récupérable de toutes les dépenses en `4365` : certaines catégories (carburant, véhicules de tourisme) sont-elles exclues du droit à déduction ? | Expert-comptable | 3 |
| Comptabilisation des sinistres | Coûts et remboursements des sinistres hors journaux (seules les dépenses qui les paient y entrent, en achats) : écritures attendues ? | Expert-comptable | 3 |
| Exercice comptable | Exercice = année civile supposée (numéros `DEP-{année}`, mois de clôture) ; qui clôt chaque mois (comptable) et qui rouvre (gérant, avec motif) | Expert-comptable / agence | 3 |
| Justificatifs des dépenses | Conservés dix ans avec les factures dans le conteneur immuable `invoices` ([ADR-0063](adr/0063-depenses-justificatifs-et-couts-des-vehicules.md)) : durée à confirmer | Expert-comptable | 3 |

## Plateforme multi-agences (ADR-0067)

Ouverts par le passage en service vendu à plusieurs agences ([ADR-0067](adr/0067-plateforme-multi-agences-cloisonnement-support-modules.md)). Les partenaires restent configurés une fois pour toute la plateforme ; chaque point devient nécessaire quand une deuxième agence réelle ouvre le module concerné.

| Sujet | À trancher | Avec qui |
|---|---|---|
| Paiement en ligne | Un contrat commerçant Clic to Pay par agence (identifiants par agence dans le Key Vault, choix de la passerelle selon l'agence du paiement) ; d'ici là, une seule boutique encaisse pour la plateforme | Exploitant, agences, STB |
| WhatsApp | Un numéro WhatsApp Business par agence (routage du webhook par `phone_number_id`) ; d'ici là, un seul numéro sert l'agence désignée par `WhatsApp:AgencyId` | Exploitant, Meta |
| Booking.com et canaux | Compte fournisseur et certification par agence ; un canal appartient déjà à une agence | Agences, Booking.com |
| Facturation électronique TTN | Adhésion et certificat par agence (chaque agence est le vendeur de ses factures) | Agences, expert-comptable |
| E-mails | Adresse d'expédition et domaine vérifié par agence (SPF, DKIM, DMARC) ; d'ici là, l'adresse de la plateforme signe pour toutes | Exploitant |
| Domaines | ✅ 30/10/2026 : la vitrine d'une agence est servie sur ses propres noms de domaine, rattachés par le support depuis la fiche de l'agence ([ADR-0069](adr/0069-domaines-propres-par-agence.md)). Reste par domaine, à la charge de l'exploitant : CNAME vers la plateforme et liaison du domaine à l'App Service avec son certificat (certificat géré ou certificat d'origine Cloudflare, [README de l'infrastructure](../infra/README.md#8-bis-domaine-propre-dune-agence)), déclaration du domaine dans le widget Turnstile (le serveur vérifie le nom d'hôte du défi), règle de cache Cloudflare ; à trancher plus tard : rediriger `rental.app/agence/…` vers le domaine propre (aujourd'hui l'adresse canonique seule le désigne) | Exploitant |
| Page d'accueil de la plateforme | Contenu de `rental.app/` (présentation du service, liste des agences ou non) | Exploitant |
| Défense en profondeur | Sécurité au niveau des lignes de SQL Server (`SESSION_CONTEXT`) en plus du filtre EF Core et du garde d'écriture | Exploitant |
| Données partagées entre agences | Liste noire commune des clients indélicats (déclaration INPDP, accord des agences) : aujourd'hui chaque agence a la sienne | Agences, INPDP |
| Facturation du service | Abonnement des agences à la plateforme (modules facturés, période d'essai) : hors de la plateforme pour l'instant | Exploitant |

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
| 12 | Le prompt demande `IChatClient` « avec invocation de fonctions » pour l'agent WhatsApp. | `IChatClient` est le port du modèle (SDK Anthropic, vérifié), mais la boucle d'outils est explicite et bornée par l'orchestrateur, sans invocation automatique : plafonds, validation des arguments, garde-fous ([ADR-0055](adr/0055-port-du-modele-sdk-anthropic-cache-et-couts.md)). |
| 13 | L'annexe B met la date (« Nous sommes le {{TODAY}} ») dans le prompt système, que la conception veut mettre en cache sans la date. | Fichier du prompt identique à l'annexe ; la phrase de la date est retirée du préfixe au rendu et donnée dans le contexte variable de chaque tour ([ADR-0055](adr/0055-port-du-modele-sdk-anthropic-cache-et-couts.md)). |
