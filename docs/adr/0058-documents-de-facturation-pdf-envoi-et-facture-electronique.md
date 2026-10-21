# ADR-0058 — Documents de facturation : PDF, envoi au client et facturation électronique TTN

- **Statut** : Acceptée (schéma TEIF, signature et transport TTN en attente de l'adhésion de l'agence)
- **Date** : 2026-10-21
- **Source** : [spécifications](../SPEC.md) §8 et §14, [prompt](../PROMPT.md) §9.4 et §16, [ADR-0010](0010-conformite-ttn-inpdp.md), [ADR-0013](0013-outbox-traitee-par-hangfire.md), [ADR-0043](0043-outbox-pompe-en-processus.md), [ADR-0057](0057-factures-avoirs-numerotation-et-moments-de-facturation.md), [conception phase 3](../conception/phase-3-finance.md) §1

## Contexte

Une facture émise doit exister sous trois formes : le PDF remis au client et conservé, l'e-mail qui le lui envoie, et — dès que l'agence a adhéré à Tunisie TradeNet — sa version électronique (XML TEIF signé, transmise à TTN, qui renvoie une référence unique). Tant que l'adhésion n'est pas finalisée, la note générale n° 2/2026 admet un régime papier transitoire avec une mention sur la facture. Le format TEIF, la signature électronique (certificat de l'agence) et le transport ne sont livrés qu'avec l'adhésion : rien de tout cela ne peut être inventé. Ces étapes appellent des services externes (stockage, messagerie, TTN) qui peuvent échouer ; elles ne doivent ni bloquer l'émission, ni la défaire.

## Décision

### Étapes après l'émission

- L'émission d'une facture ou d'un avoir publie `InvoiceIssued` / `CreditNoteIssued` dans l'outbox (même transaction). Le gestionnaire exécute `BillingDocumentPipeline`, dans sa propre portée DI, comme `system:billing` : **(1)** avec TTN, la transmission ; **(2)** le PDF, stocké une fois imprimable (aussitôt en papier, après la référence TTN sinon) ; **(3)** l'e-mail à l'acheteur. Chaque étape est faite **au plus une fois** (état enregistré sur le document) et **rejouable** : un rejeu après une panne entre l'envoi du fichier et son enregistrement réécrit le même fichier (le chemin est le numéro).
- Un échec est **enregistré sur le document** (tentatives, dernière erreur), puis renvoyé à l'outbox, qui réessaie avec son délai croissant et liste le message s'il échoue toujours ; l'onglet « Facturation électronique » de `/admin/invoices` montre les documents en attente ou en échec et un comptable (`invoices.manage`) peut demander une nouvelle tentative (`BillingDocumentRetryRequested`).

### PDF

- **QuestPDF** (licence Community, à confirmer avec le chiffre d'affaires de l'agence), rendu par l'hôte web (`IBillingDocumentRenderer`) : vendeur et son identité légale, acheteur, numéro et date, dossier et période (ou facture corrigée et motif pour un avoir), lignes avec prix unitaire et montant HT, TVA par taux, totaux et droit de timbre, situation des paiements à l'émission, référence TTN ou mention du régime transitoire, mentions, pied de page de l'agence. Original **en français** ; une **traduction de courtoisie en anglais**, marquée comme telle, est rendue à la demande et jamais conservée.
- Stocké dans le conteneur privé **`invoices`**, chemin `{exercice}/{numéro}.pdf`, avec son empreinte SHA-256 et sa taille. Servi par `/admin/invoices/{id}/pdf` et `/admin/credit-notes/{id}/pdf` aux seuls détenteurs de `invoices.read` dans une session MFA, avec `ETag` (empreinte) et `Cache-Control: private, no-cache` ; la traduction en `no-store`.

### E-mail

- L'e-mail (français puis anglais) est mis en file par `IEmailQueue` avec le PDF **joint par référence** (conteneur et chemin) : le transport le lit au moment de l'envoi. C'est un message de service de la location : aucun consentement marketing n'est demandé. Sans adresse de l'acheteur, rien n'est envoyé ; le PDF reste disponible.

### Facturation électronique

- Port **`IEInvoiceProvider`** (mode, `TransmitAsync` du document électronique → référence et instant, ou erreur), choisi par agence : `billing.einvoice_ttn` (faux par défaut).
  - **Papier transitoire** (`PaperTransitional`, par défaut) : aucune transmission ; état « non requis » ; le PDF porte la mention `billing.einvoice_transitional_mention` (`TODO(spec)` : libellé validé par l'expert-comptable).
  - **TTN** (`Ttn`) : adaptateur réel **vide à dessein**, qui répond `billing.einvoice.not_configured` : une agence basculée trop tôt échoue de façon visible (documents en échec, nouvelles tentatives) au lieu d'échanger un format deviné. `TODO(spec)` : version du schéma TEIF et correspondance de nos champs, signature XAdES avec le certificat de l'agence, transport (adresses, authentification, idempotence par numéro), référence retournée et codes d'erreur.
  - **Faux TTN** (`FakeTtnEInvoiceProvider`), uniquement en développement et en test (`Billing:Ttn:UseFake`, ignoré ailleurs) : accepte tout et renvoie `TTN-FAKE-{numéro}` ; il ne produit aucun XML.

## Conséquences

- L'émission ne dépend d'aucun service externe : une facture est numérotée et figée même si le stockage, la messagerie ou TTN sont indisponibles ; ses documents suivent dès qu'ils reviennent.
- Le jour où TTN est configuré, seul l'adaptateur change : les étapes, les rejeux, l'écran des échecs et les tests (qui tournent sur le faux) restent.
- Un document en échec TTN n'a pas encore de PDF : on ne remet pas au client une facture sans la référence que la loi exige en régime électronique.
- Les PDF des factures sont des pièces comptables conservées (pas de purge automatique).

## Alternatives écartées

- **Générer le PDF dans la transaction d'émission** : lie l'émission au stockage et à QuestPDF ; un échec ferait perdre le numéro ou laisserait un fichier orphelin.
- **PDF régénéré à chaque consultation** : l'original remis au client doit rester identique (empreinte) même si un libellé ou un gabarit change.
- **XML TEIF approximatif « en attendant »** : un format deviné serait rejeté ou, pire, accepté avec des erreurs ; l'adaptateur vide échoue franchement.
- **Pièce jointe copiée dans le message d'outbox** : alourdit l'outbox ; la référence au conteneur privé suffit.
