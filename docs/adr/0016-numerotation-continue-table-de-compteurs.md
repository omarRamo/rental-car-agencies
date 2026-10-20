# ADR-0016 — Numérotation continue des pièces par table de compteurs

- **Statut** : Acceptée
- **Date** : 2026-10-20
- **Source** : [spécifications](../SPEC.md) (facturation : « numéroter sans trou par exercice »), [prompt](../PROMPT.md) section 5

## Contexte

Dossiers, factures et avoirs portent des numéros lisibles : `LOC-2027-000123`, `FAC-2027-000045`, `AV-2027-000003`. La loi impose une numérotation des factures continue, sans trou, par exercice. Le prompt propose des séquences SQL, mais une séquence SQL Server n'est pas continue : une valeur consommée par une transaction annulée est perdue, et le cache de la séquence saute des valeurs après un redémarrage ou une bascule.

## Décision

- **Table `infra.DocumentCounters`** (clé : type de pièce et exercice ; dernière valeur). `IDocumentNumberGenerator.NextAsync(type, exercice)` incrémente le compteur par un `MERGE … WITH (HOLDLOCK) … OUTPUT inserted.LastValue` **dans la transaction de l'appelant** : si elle est annulée, le numéro l'est aussi et sera réattribué ; aucun trou.
- L'appel exige une transaction ouverte (`InvalidOperationException` sinon) : on l'appelle dans `IUnitOfWork.ExecuteAsync(...)` ([ADR-0017](0017-execution-cas-usage-portee-transactions.md)), de préférence en fin de transaction pour tenir le verrou de ligne le moins longtemps possible.
- **Format** : objet valeur `DocumentNumber` (`{préfixe}-{AAAA}-{NNNNNN}`, préfixes `LOC`, `FAC`, `AV`, six chiffres au moins). L'exercice est l'année civile dans le fuseau `Africa/Tunis` (`NextForDateAsync`).
- Même mécanisme pour les trois types, même si un trou serait sans conséquence pour les dossiers.

## Conséquences

- L'émission d'un même type pour un même exercice est sérialisée par le verrou de ligne : sans effet à ce volume.
- Tests d'intégration sur SQL Server réel : numéros consécutifs, numéro d'une transaction annulée réattribué, douze transactions concurrentes sans doublon ni trou, appel hors transaction refusé, nouvelle tentative après une panne passagère sans trou.
- Écart avec le prompt (séquences SQL) consigné dans les [points ouverts](../points-ouverts.md).

## Alternatives écartées

- **Séquence SQL** : trous à l'annulation et au redémarrage, non conforme pour les factures.
- **`MAX(numéro) + 1`** : course entre transactions sans isolement sérialisable, et lecture de toute la table.
- **Colonne d'identité** : mêmes trous que la séquence.
