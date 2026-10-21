using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Seeding;
using CarRental.Domain.Messaging.Agent;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Messaging.Agent.Knowledge;

/// <summary>
/// First answers of the knowledge base, taken from the FAQ of the public site (<c>FaqContent</c>, public resources),
/// seeded as <b>drafts</b> in every environment: the bot ignores them until a manager reviews and validates them in
/// <c>/admin/whatsapp/knowledge</c>, so that nothing unvalidated is ever said. Figures that depend on the settings or
/// the fleet (ages, deposits) are left out of the texts. A topic already present is never touched again.
/// </summary>
internal sealed class KnowledgeSeeder(IAppDbContext db, TimeProvider clock) : IDataSeeder
{
    public SeedStage Stage => SeedStage.Reference;

    public int Order => 210;

    internal static IReadOnlyList<(string Topic, string Title, string Keywords, string French, string English)> Drafts { get; } =
    [
        ("documents", "Documents requis", "documents, papiers, pièce d'identité, CIN, passeport, permis",
            "Pour chaque conducteur : sa pièce d'identité (CIN pour un Tunisien, passeport pour une autre nationalité) et son permis de conduire, recto et verso, valides jusqu'à la date de retour. Un permis international est exigé si votre permis n'est pas rédigé en caractères latins. Les photos se déposent en ligne, depuis votre téléphone.",
            "For each driver: an identity document (national ID card for Tunisians, passport for other nationalities) and a driving licence, front and back, valid until the return date. An international driving permit is required if your licence is not written in Latin characters. You upload the photos online, from your phone."),
        ("caution", "Caution", "caution, dépôt, garantie, franchise",
            "Oui, une caution est prise à la remise du véhicule et restituée au retour. Son montant dépend de la catégorie ; elle est réduite si vous prenez le rachat de franchise.",
            "Yes, a deposit is taken when you pick up the car and returned when you bring it back. Its amount depends on the category; it is reduced with the franchise waiver."),
        ("paiement", "Paiement", "paiement, payer, carte bancaire, Clic to Pay",
            "Par carte bancaire, sur la page sécurisée de Clic to Pay : le montant est réservé sur votre carte quand vous envoyez la demande, puis débité seulement quand un agent la valide. Nous ne voyons et ne conservons jamais votre numéro de carte.",
            "By bank card, on the secure Clic to Pay page: the amount is held on your card when you send the request, and charged only when an agent validates it. We never see nor keep your card number."),
        ("confirmation", "Confirmation de la réservation", "confirmation, validation, délai",
            "Un agent vérifie votre demande et vos documents, en général sous 2 heures ouvrées. Vous recevez alors la confirmation par e-mail, et par WhatsApp si vous l'avez accepté.",
            "An agent checks your request and your documents, usually within 2 business hours. You then receive the confirmation by e-mail, and on WhatsApp if you accepted it."),
        ("annulation", "Annulation", "annulation, annuler, remboursement",
            "Oui, depuis « Ma réservation » ou en nous contactant, selon les conditions générales de location : les frais éventuels sont affichés avant que vous confirmiez l'annulation.",
            "Yes, from \"My booking\" or by contacting us, according to the rental terms: any fee is shown before you confirm the cancellation."),
        ("aller_simple", "Retour dans un autre lieu", "aller simple, autre lieu, rendre ailleurs",
            "Oui : indiquez un autre lieu de retour dans la recherche. Le supplément aller simple est inclus dans le prix affiché.",
            "Yes: choose another return location in the search. The one-way fee is included in the price shown."),
        ("territoire", "Sortie du territoire", "frontière, Algérie, Libye, étranger",
            "Non, les véhicules ne peuvent pas quitter le territoire tunisien.",
            "No, the cars cannot leave Tunisian territory."),
        ("prix", "Ce que comprend le prix", "prix final, inclus, TVA, contribution",
            "Le total affiché comprend la location pour toute la durée, la TVA et la contribution journalière due à l'État. Les options que vous choisissez (conducteur additionnel, siège enfant, rachat de franchise, livraison) s'y ajoutent et le total est recalculé avant le paiement.",
            "The total shown includes the rental for the whole period, VAT and the daily contribution owed to the State. The extras you choose (additional driver, child seat, franchise waiver, delivery) are added to it and the total is recalculated before payment."),
        ("age", "Âge et permis du conducteur", "âge, jeune conducteur, ancienneté du permis",
            "Un âge minimum et une ancienneté de permis minimale sont exigés, et un supplément jeune conducteur peut s'appliquer : il est inclus dans le prix dès que vous indiquez la date de naissance du conducteur.",
            "A minimum age and a minimum licence seniority are required, and a young driver fee may apply: it is included in the price as soon as you enter the driver's date of birth."),
    ];

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var existing = await db.Set<KnowledgeEntry>().Select(e => e.TopicKey).ToListAsync(cancellationToken);
        var now = clock.GetUtcNow();
        foreach (var (topic, title, keywords, french, english) in Drafts.Where(d => !existing.Contains(d.Topic)))
        {
            var content = KnowledgeContent.Create(title, keywords, french, english, null).Value;
            db.Set<KnowledgeEntry>().Add(KnowledgeEntry.CreateDraft(topic, content, now).Value);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
