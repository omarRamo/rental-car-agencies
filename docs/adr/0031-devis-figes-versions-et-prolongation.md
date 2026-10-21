# ADR-0031 — Devis figés : agrégat `Quote` immuable, versions par dossier et règle de prolongation

- **Statut** : Acceptée
- **Date** : 2026-10-21
- **Source** : [spécifications](../SPEC.md) §5, §6, §7 et §11, [prompt](../PROMPT.md) §6 (invariant 5 « un devis accepté ne change pas »), [conception phase 1](../conception/phase-1-exploitation.md) §2 et §4, [ADR-0023](0023-plans-tarifaires-versionnes.md), [ADR-0024](0024-moteur-de-prix-pur-et-son-alimentation.md)

## Contexte

Le moteur de prix ([ADR-0024](0024-moteur-de-prix-pur-et-son-alimentation.md)) calcule un `PriceQuote` à la demande, au plan publié du moment. Un dossier doit au contraire garder le prix accepté par le client, même si le plan change ensuite, et pouvoir être modifié (dates, catégorie, options, prolongation, remise) sans perdre l'historique. Le bot et le tunnel public ont besoin d'un prix garanti 24 heures, désigné par un code court. Il reste à décider où vit ce prix figé, ce qu'il contient, et comment l'on chiffre une prolongation sans remettre en cause le prix déjà accepté.

## Décision

### Un agrégat immuable

- `Quote` (schéma `bookings`, table temporelle) n'a **aucun mutateur** : son contenu est vérifié une fois, à la création (les lignes doivent faire les totaux), puis ne change plus. Deux usages :
  - **devis verrouillé** (`Locked`, sans dossier) : prix garanti `Quote.LockDuration` (24 h) depuis sa création, désigné par un **code public de six caractères** d'un alphabet sans ambiguïté (sans 0/O ni 1/I/L, environ 887 millions de codes, tiré par un générateur cryptographique, unique en base) ;
  - **version de dossier** (`BookingVersion`) : identifiant du dossier, numéro de version 1, 2, 3… (unique par dossier) et origine — `Initial`, `LockedQuote` (copie d'un devis verrouillé, qui reste intact, avec `SourceQuoteId`), `Requote`, `Extension`, `ManualDiscount`.
- **Contenu figé** : les lignes (nature, code, clé de libellé et ses arguments, quantité, prix unitaire TTC, HT, TVA, TTC, taux), les totaux HT/TVA/TTC, la caution (hors total) et le rachat de franchise, le plan et sa version, le résultat du code promo et les promotions appliquées, et les instantanés JSON des entrées, des promotions et des prix journaliers. Les libellés sont des **clés de ressources** traduites à l'affichage : un devis se relit en français ou en anglais, à l'identique, des années après.
- Le dossier référence sa version courante et garde ses totaux (listes, paiements, chronologie). La caution reste hors du total TTC.

### Versions

- **Aucun changement d'une entrée du prix** (payeur, notes) : pas de nouvelle version.
- **Avenant avant le départ** (dates, lieux, catégorie, options, code promo, conducteurs, modèle garanti) : tout le dossier est **recalculé au plan en vigueur** et devient une nouvelle version `Requote`. Une remise manuelle d'une version antérieure n'est pas reportée : l'agent la redonne s'il le souhaite (l'historique reste consultable).
- **Remise manuelle** : une ligne négative TTC (HT et TVA au taux du devis), un motif obligatoire gardé sur la version, la permission `bookings.discount` ; jamais plus que le total. Elle peut accompagner un avenant dans la même version.
- **Prix affiché, prix enregistré** : l'écran transmet le total que l'agent a vu ; si le moteur donne un autre prix au moment d'enregistrer (plan publié entre-temps), l'écriture est refusée (`booking.quote.price_changed`) et l'agent revoit le prix.

### Règle de prolongation (location en cours)

- Les **lignes figées sont conservées**, et les jours ajoutés valent `devis(nouvelle période complète, maintenant) − devis(période d'origine, maintenant)`, les deux calculés au plan en vigueur avec les mêmes entrées (catégorie, lieux, options, conducteurs).
- Cette différence est répartie en **deux lignes** : une ligne de prolongation (location, options et suppléments des jours ajoutés) et une ligne de **contribution journalière** pour les jours ajoutés, gardée à part pour la comptabilité et la facture électronique ([ADR-0010](0010-conformite-ttn-inpdp.md)). La nouvelle version reprend la période, le nombre de jours, le plan et les entrées du devis de la nouvelle période.
- Les autres avenants pendant la location sont refusés (`booking.amendment.extension_only`) ; l'échange de véhicule passe par l'affectation, avec motif.

## Conséquences

- Invariant 5 tenu par construction : un devis n'est jamais réécrit ; chaque changement de prix laisse la version précédente consultable (écran « historique des versions »).
- La prolongation respecte le prix accepté pour la période d'origine tout en valorisant les jours ajoutés aux saisons, événements et paliers de durée du moment : la différence intègre l'effet du palier atteint par la durée totale.
- Un devis verrouillé repris par une demande est copié (avec `SourceQuoteId`) : il reste la preuve du prix garanti tant qu'il n'a pas expiré, et chaque reprise est traçable. Une demande arrivée après son expiration est refusée (`booking.quote.expired`) : le client demande un nouveau prix.
- Les codes courts sont lisibles au téléphone et sur WhatsApp ; leur unicité est garantie par un index unique, un tirage en collision est simplement recommencé.

## Alternatives écartées

- **Recalculer tout le dossier à la prolongation** : changerait le prix déjà accepté de la période d'origine (un plan plus cher, une saison passée) ; contraire à l'invariant 5.
- **Prorata du prix journalier figé** : ignore les saisons, événements, paliers et la contribution journalière du plan en vigueur.
- **Prix stocké sur le dossier, sans agrégat** : pas d'historique des versions, pas de devis autonome pour le bot et le tunnel.
- **Libellés figés en texte** : le devis ne se relirait que dans la langue de sa création.
