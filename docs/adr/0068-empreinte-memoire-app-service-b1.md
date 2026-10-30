# ADR-0068 — Empreinte mémoire sur une seule instance App Service B1

- **Statut** : Acceptée
- **Date** : 2026-10-30
- **Source** : revue globale du 30/10/2026 (« une solution qui gère très bien la mémoire ») ; précise l'[ADR-0004](0004-hebergement-app-service-b1-cloudflare.md) (hébergement) et l'[ADR-0044](0044-caches-parametres-et-plans-publies.md) (caches)

## Contexte

La plateforme tourne sur **une** instance App Service B1 Linux : un cœur et 1,75 Go partagés par Kestrel, les circuits Blazor Server du back office (un circuit par onglet ouvert, avec son état MudBlazor et sa portée DI), le serveur Hangfire, l'exécuteur des tours du bot WhatsApp et les caches en mémoire (paramètres, plans publiés, disponibilités, faits publics des agences, annuaire des agences). Avec plusieurs agences servies par le même processus, ce qui n'était qu'un confort devient une limite : un dépassement de mémoire redémarre l'application pour toutes les agences à la fois.

Les choix par défaut du SDK Web ne sont pas faits pour ce gabarit : ramasse-miettes serveur (un tas par cœur, mémoire rendue tardivement), 100 circuits déconnectés gardés trois minutes, corps de requête acceptés jusqu'à 30 Mo, cache mémoire sans borne.

## Décision

- **Ramasse-miettes** : mode station de travail concurrent (`ServerGarbageCollection=false`, `ConcurrentGarbageCollection=true`) et `System.GC.ConserveMemory=5` (`runtimeconfig.template.json`) : un seul tas, compacté avant de grossir, mémoire rendue au système ; `TieredPGO` gardé. Sur un cœur le mode serveur n'apporte rien et réserve davantage.
- **Circuits Blazor** : bornés par la section `Blazor` (`DisconnectedCircuitMaxRetained` 30, `DisconnectedCircuitRetentionPeriod` 2 min, `MaxBufferedUnacknowledgedRenderBatches` 10), validée au démarrage. Un onglet fermé ou un réseau perdu libère sa mémoire en deux minutes ; au-delà de trente circuits en attente, le plus ancien est évincé (l'utilisateur recharge la page).
- **Corps de requête** : Kestrel refuse au-delà de la plus grande entrée lue par la plateforme (une photo de pièce, 10 Mo plus l'enveloppe multipart, `PublicForms.UploadRequestLimit`) ; les points d'entrée qui lisent moins (webhook WhatsApp, canaux) gardent leur propre limite plus basse.
- **Caches** : le cache mémoire de l'hôte porte une taille maximale (1 000 entrées de taille 1 ; une entrée par vitrine ouverte) ; le cache de disponibilité purge toutes les 256 lectures les instantanés qu'aucun lecteur n'a rafraîchis depuis deux fois leur expiration (catégorie supprimée ou inactive). Les autres caches restent un instantané unique pour toutes les agences (paramètres, annuaire) ou indexés par une clé bornée par les données (plans publiés par agence et canal).
- **Lecture sans suivi** : les requêtes de lecture restent en `AsNoTracking` et les agrégats à collections en requêtes fractionnées (ADR-0028) ; un composant interactif appelle les services par `IOperationRunner` (portée courte, `DbContext` libéré) et jamais par injection directe.
- **Mesure** : le compteur `process.runtime.dotnet.gc.heap.size` et les métriques runtime déjà exportées (`AddRuntimeInstrumentation`) servent de référence ; une alerte Azure Monitor sur la mémoire de l'App Service est à régler comme les autres (ADR-0060).

## Conséquences

- Les réglages se changent sans recompiler (`Blazor__*` dans les paramètres de l'App Service) ; une valeur hors bornes empêche le démarrage.
- Un envoi de plus de 10,25 Mo est refusé par Kestrel (413) avant d'atteindre l'application ; les écrans du back office qui envoient des photos passent par le flux JS interop de Blazor, hors de cette limite.
- Passer à plusieurs instances (ADR-0004) exigerait toujours Azure SignalR et un cache partagé : rien ici ne l'anticipe ni ne l'empêche.

## Alternatives écartées

- **Limite dure du tas (`GCHeapHardLimit`)** : un dépassement devient une exception hors mémoire au lieu d'un ralentissement ; sur une seule instance, mieux vaut la pression progressive de `ConserveMemory`.
- **Blazor WebAssembly ou auto pour le back office** : supprimerait les circuits serveur mais change la pile, la sécurité (API complète à exposer) et le volume téléchargé ; hors de propos pour trente véhicules par agence.
- **`AddDbContextPool`** : incompatible avec l'injection du contexte utilisateur dans le `DbContext` (filtre d'agence) ; le gain serait marginal.
