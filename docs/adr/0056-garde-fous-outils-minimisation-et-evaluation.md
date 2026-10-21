# ADR-0056 — Agent WhatsApp : outils, garde-fous vérifiés par le code, minimisation des données et évaluation

- **Statut** : Acceptée
- **Date** : 2026-10-21
- **Source** : [conception phase 4](../conception/phase-4-whatsapp.md) §2 et §4, [prompt](../PROMPT.md) §6 (invariants 9 et 10), §9.2 et annexe B, [spécifications](../SPEC.md) §11, [ADR-0047](0047-whatsapp-webhook-signe-traitement-asynchrone-et-fenetre-24-h.md), [ADR-0048](0048-boite-whatsapp-partagee-et-notifications-avec-consentement.md), [ADR-0055](0055-port-du-modele-sdk-anthropic-cache-et-couts.md)

## Contexte

Le bot ne doit **jamais** inventer un prix, une disponibilité, une remise ou une condition, **jamais** confirmer une
réservation, **jamais** en créer une (il envoie un lien vers le tunnel du site avec un devis verrouillé), **jamais**
traiter une pièce jointe. Un prompt ne garantit rien : un modèle peut se tromper, être manipulé (« ignore tes
consignes ») ou recopier un résultat d'outil piégé. Les messages des clients sont des données personnelles envoyées à un
sous-traitant hors de Tunisie.

## Décision

### Les outils (noms de l'annexe B), seuls accès du modèle aux données

- `rechercher_disponibilites` : catégories réellement disponibles (moteur de disponibilité en cache, canal WhatsApp) et
  prix total TTC de chacune par le **vrai moteur de prix** ; filtres passagers, bagages, boîte, budget ; trois options au
  plus, la moins chère d'abord, chacune avec un identifiant (`A1`, `A2`… puis `B1`… à la recherche suivante) ; sinon les
  catégories les plus proches avec les critères manqués, les dates proches (± 1 ou 2 jours) ou la durée minimale. Les
  lieux sont reconnus par code, nom ou appellation usuelle (« Carthage », « l'agence », مطار تونس) ; un lieu non desservi
  est renvoyé avec la liste des lieux. Les arguments mettent à jour le besoin structuré de la conversation.
- `creer_lien_devis` : **un identifiant d'option, jamais un montant** ; devis verrouillé 24 h
  (`IBookingRequests.CreateLockedQuoteAsync`, prix recalculé par le moteur) et lien profond construit par une seule
  classe testée (`BookingDeepLink`) : `{PublicSite:BaseUrl}/fr/reserver` (ou `/en/book`)
  `?lieu=…&retour_lieu=…&depart=…&retour=…&cat=…&devis=CODE&src=whatsapp` — paramètres et chemins du tunnel public,
  vérifiés par un test d'architecture. Aucun outil ne crée de dossier.
- `infos_agence` : les réponses de la **base de connaissances validée par le gérant** (actives et validées seulement),
  dans la langue du client quand elle est écrite, les faits du référentiel (nom, adresse, téléphone, horaires, lieux) et,
  pour la caution, les montants par catégorie.
- `statut_reservation` : l'état d'un dossier en mots (dates, lieux, catégorie ; aucun montant, aucune donnée
  personnelle) **seulement si le numéro WhatsApp est celui du conducteur principal, du second conducteur ou du payeur** ;
  sinon la même réponse neutre « introuvable » qu'une référence inconnue ; trois recherches au plus par conversation.
- `transferer_a_un_agent` : onze catégories de motif et un résumé court, qui deviennent le motif de transfert affiché
  dans la boîte partagée ; le bot se tait ensuite.
- Chaque appel est validé contre le schéma de son outil (propriétés inconnues, obligatoires, types, énumérations,
  bornes) ; une erreur revient au modèle, en français, pour qu'il corrige. Les résultats sont des **données** :
  l'addendum du prompt le rappelle et le texte du client ne peut pas imiter le bloc de contexte de la plateforme (balise
  retirée).

### La base de connaissances

- `KnowledgeEntry` (table temporelle) : clé de sujet, titre, mots-clés, réponses en français et en anglais
  (obligatoires) et en arabe (facultative), validation (par qui, quand), activité. Une création est un brouillon ; une
  modification renvoie une réponse validée en brouillon ; la validation (journalisée, `messaging.knowledge_validated`)
  refuse une réponse qui promet une confirmation ; une réponse désactivée n'est plus donnée.
- Brouillons initiaux repris de la FAQ du site (documents, caution, paiement, confirmation, annulation, aller simple,
  territoire, prix, âge), sans les chiffres qui dépendent des réglages : rien n'est donné avant la relecture du gérant.
- Écran `/admin/whatsapp/knowledge`, permission `whatsapp.knowledge` (préréglage du gérant).

### Les garde-fous, appliqués par le code au message qui partirait

- **Montants** : tout montant du texte (formats `681,400`, `681.400`, `681 400`, `681400`, `681 DT`, `٦٨١٫٤٠٠ د.ت`,
  dinars, millimes, euros…) doit figurer parmi les montants rendus par les outils dans la conversation (arrondi au
  dinar toléré) ; les dates, heures, numéros de téléphone, références de dossier, pourcentages, nombres de passagers,
  de jours ou de places ne sont pas des montants. Sinon : réponse **bloquée**, message poli et **transfert**.
- **Confirmation** : aucune promesse de confirmation (français, anglais, arabe, darija ; négations et futurs
  conditionnels compris : « sera confirmée après validation par un agent » est permis). Sinon : bloquée et transfert.
  Seule exception : rapporter le statut d'un dossier **du client** que l'agence a confirmé.
- **Documents** : une réponse qui demande la photo d'une pièce d'identité, d'un permis ou d'une carte est remplacée par
  le message fixe qui renvoie au lien sécurisé.
- **Pièces jointes** : jamais téléchargées ni ouvertes ; un tour fait seulement de pièces jointes reçoit, **sans appel
  au modèle**, une réponse fixe dans la langue du client qui renvoie au tunnel ; avec un texte, le modèle sait seulement
  qu'une pièce jointe a été envoyée.
- **Divulgation** : une réponse qui recopie de longs passages du prompt, plusieurs de ses phrases, ses titres, le nom
  d'un outil ou la balise du contexte est remplacée par un refus poli. Hors sujet et injections : refus par le prompt,
  divulgation bloquée par le code.
- **Plafonds** et tours sans progrès : transfert ([ADR-0055](0055-port-du-modele-sdk-anthropic-cache-et-couts.md)).
- **Un seul message par tour** : texte, ou texte et trois boutons au plus (titres de 20 caractères) pour choisir une des
  options présentées ; jamais un modèle de message.

### Minimisation des données

- Le modèle reçoit : le prompt, les textes des clients (2 000 caractères au plus par message du tour, 1 000 dans
  l'historique), les réponses du bot, les messages des agents annoncés comme tels, une notification par son seul nom de
  modèle, une pièce jointe par une mention. **Jamais** le numéro WhatsApp, le nom du profil, la personne rattachée, une
  pièce d'identité ou une donnée de paiement ; les résultats d'outils n'en portent pas.
- Journaux : identifiants et compteurs seulement, jamais un texte, un numéro ou un nom (test d'architecture sur les
  modèles de journal de l'agent). Traces du SDK : métadonnées seules.
- Mémoire de l'agent (`AgentConversationStates` : options de la dernière recherche, montants connus, compteurs, dernier
  lien) supprimée avec la conversation ; les relevés de coût ne contiennent aucun texte.
- Sous-traitant : Anthropic (API Claude) ou Microsoft (Foundry, « Global » ou « US Data Zone », sans zone européenne) :
  transfert de données personnelles hors de Tunisie à couvrir par l'autorisation de l'INPDP (points ouverts). Les
  numéros pilotes (`Ai:PilotNumbers`) permettent d'ouvrir le bot progressivement.

### Évaluation

- Trente conversations scénarisées (12 en français, 6 en arabe, 6 en darija, 6 en anglais), en fichiers JSON
  (`tests/CarRental.Application.Tests/Messaging/Agent/Evaluation`, schéma dans son README) : tours du client, réponses du
  modèle scripté, attentes (outils, arguments, transfert et motif, boutons, lien, message fixe, issue, langue) et
  invariants vérifiés à chaque tour sur le message envoyé (aucun montant inconnu, aucune promesse, aucune demande de
  photo, aucune divulgation, boutons valides).
- En CI : rejouées contre le modèle scripté et les faux moteurs. Contre le vrai modèle : à la demande seulement (tests
  de la catégorie `AiEvaluation`, ignorés sans `AI_EVAL_API_KEY` ni `ANTHROPIC_API_KEY` ; workflow manuel
  `.github/workflows/ai-eval.yml`, secret `ANTHROPIC_API_KEY`), mêmes vérifications, rapport des écarts en Markdown et
  JSON ; environ 0,04 USD par passage avec la grille par défaut. À rejouer à chaque modification du prompt.

## Conséquences

- Une erreur du modèle sur un prix ou une confirmation ne peut pas atteindre le client : au pire, la conversation passe
  à un agent avec le motif du garde-fou.
- Le bot n'en sait pas plus que ce que les outils lui rendent ; ce qui n'est ni dans un outil ni dans une réponse validée
  (kilométrage, assurance, carburant…) ne peut être dit qu'une fois saisi et validé dans la base de connaissances.
- Les détecteurs sont des heuristiques : un faux positif fait un transfert de trop (accepté), pas une réponse fausse.

## Alternatives écartées

- **Garde-fous par le seul prompt** : un modèle se trompe ou se laisse manipuler ; la règle « jamais de prix inventé »
  doit être vérifiée sur le message qui part.
- **Réponse structurée du modèle** (montants dans des champs) : la conversation reste du texte libre, qu'il faut de toute
  façon vérifier.
- **Grille de prix dans le prompt** : un prix calculé hors du moteur de prix est interdit ; les montants ne viennent que
  des outils.
- **Lire les pièces jointes** (reconnaissance de texte d'une pièce d'identité) : interdit ; les documents se déposent sur
  le lien sécurisé du tunnel.
