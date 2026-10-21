using CarRental.Domain.Messaging.Agent;

namespace CarRental.Domain.Tests.Messaging.Agent;

/// <summary>
/// The amount guardrail: an amount of a reply must be one the tools returned (PROMPT §6, invariant 10), whatever the
/// language and the way it is written; dates, times, phone numbers and counted quantities are never amounts.
/// </summary>
public sealed class AmountGuardTests
{
    private static readonly decimal[] Known = [681.400m, 800.000m, 95.000m, 1250.500m];

    [Theory]
    [InlineData("Le total TTC est de 681,400 TND pour 5 jours.")]
    [InlineData("Total : 681.400 DT, caution 800 DT.")]
    [InlineData("Prix total 681 400 TND.")]
    [InlineData("Prix total : 681400 TND")]
    [InlineData("Soit 681,4 dinars au total.")]
    [InlineData("Environ 681 DT pour tout le séjour.")]
    [InlineData("Caution : 1 250,500 TND (rendue au retour).")]
    [InlineData("Caution : 1.250,500 TND.")]
    [InlineData("Le total est de 681,400.")]
    [InlineData("السعر الجملي ٦٨١٫٤٠٠ د.ت لمدة ٥ أيام")]
    [InlineData("المبلغ: ٦٨١٬٤٠٠ دينار")]
    [InlineData("الضمان 800 د.ت")]
    [InlineData("Essoum 681,400 DT w el caution 800 DT, 3andek 5 ayem.")]
    [InlineData("9adech ? 681.400 dt kol chay")]
    [InlineData("The total price is TND 681.400, deposit TND 800.")]
    [InlineData("Total: 681.4 dinars for 5 days.")]
    [InlineData("1. Économique (5 places) : 681,400 TND\n2. Compacte : 95 DT par jour")]
    public void Amounts_returned_by_the_tools_pass_in_every_language_and_format(string reply)
    {
        AmountMentions.Find(reply).ShouldNotBeEmpty(reply);
        AmountGuard.Unknown(reply, Known).ShouldBeEmpty(reply);
    }

    [Theory]
    [InlineData("Le total est de 650 TND.", "650")]
    [InlineData("Je peux vous faire 600 DT au lieu de 681,400 DT.", "600")]
    [InlineData("Avec la remise, cela fait 612,000 TND.", "612,000")]
    [InlineData("Le prix est de 700.", "700")]
    [InlineData("السعر ٦٥٠ د.ت فقط", "650")]
    [InlineData("بسعر 590 دينار", "590")]
    [InlineData("Ken t7eb n3amlek 550 dt", "550")]
    [InlineData("Essoum 620 kahaw", "620")]
    [InlineData("The total is 599 TND.", "599")]
    [InlineData("That would be about 210 € for the week.", "210")]
    [InlineData("Price: 682 DT.", "682")]
    [InlineData("Prix : 681,500 TND", "681,500")]
    public void An_invented_amount_is_caught(string reply, string invented)
    {
        var unknown = AmountGuard.Unknown(reply, Known);

        unknown.ShouldNotBeEmpty(reply);
        unknown.ShouldContain(m => m.Text == invented, reply);
    }

    [Theory]
    [InlineData("Départ le 01/07/2027 à 10:00, retour le 05/07/2027 à 10h.")]
    [InlineData("Du 2027-07-01T10:00 au 2027-07-05T10:00.")]
    [InlineData("Le prix est garanti 24 heures, réponse sous 2 heures ouvrées.")]
    [InlineData("Appelez-nous au +216 70 000 000 ou au 98 123 456.")]
    [InlineData("Pour 4 personnes et 2 valises, 5 places, 200 km par jour.")]
    [InlineData("Votre dossier LOC-2027-000123 est en cours de validation.")]
    [InlineData("Le lien : https://www.example.tn/fr/reserver?lieu=TUN&depart=2027-07-01T10:00&retour=2027-07-05T10:00&cat=ECMR&devis=Q7F3K2&src=whatsapp")]
    [InlineData("La TVA de 19 % est incluse dans le prix.")]
    [InlineData("Prix du 21 au 28 juillet : demandez une recherche.")]
    [InlineData("Le prix pour le 1er juillet 2027 dépend de la saison.")]
    [InlineData("Option 2, catégorie 3 : quel prix vous convient ?")]
    [InlineData("حجز لـ ٤ أشخاص من ١/٧ إلى ٥/٧ على الساعة ١٠:٠٠")]
    [InlineData("Nheb karhba l 4 personnes men 1 lel 5 juillet, 9adech ?")]
    [InlineData("For 3 days from 07/01 at 10 am, call +216 71 234 567.")]
    [InlineData("Le prix 2027 sera publié bientôt.")]
    public void Dates_times_phones_codes_and_quantities_are_not_amounts(string reply)
    {
        AmountGuard.Unknown(reply, Known).ShouldBeEmpty(reply);
    }

    [Fact]
    public void A_foreign_currency_never_matches_a_price_of_the_engine()
    {
        AmountGuard.Unknown("Soit 681,400 € environ.", Known).ShouldHaveSingleItem().IsForeignCurrency.ShouldBeTrue();
        AmountGuard.Unknown("About $95 a day.", Known).ShouldNotBeEmpty();
    }

    [Fact]
    public void A_reply_without_amount_has_no_mention()
    {
        AmountMentions.Find("Bonjour ! Pour quelles dates et quel lieu de prise en charge ?").ShouldBeEmpty();
        AmountMentions.Find(null).ShouldBeEmpty();
        AmountMentions.Find("   ").ShouldBeEmpty();
    }

    [Theory]
    [InlineData("681,400", false, new double[] { 681.4, 681400 })]
    [InlineData("681.400", false, new double[] { 681.4, 681400 })]
    [InlineData("681 400", false, new double[] { 681.4, 681400 })]
    [InlineData("681400", false, new double[] { 681.4, 681400 })]
    [InlineData("681,4", false, new double[] { 681.4 })]
    [InlineData("1.250,500", false, new double[] { 1250.5 })]
    [InlineData("1 250 000", false, new double[] { 250, 1250, 1250000 })]
    [InlineData("500", true, new double[] { 0.5 })]
    public void A_written_number_stands_for_every_value_it_may_mean(string written, bool millimes, double[] expected)
    {
        var candidates = AmountMentions.Candidates(written, millimes);

        foreach (var value in expected)
        {
            candidates.ShouldContain((decimal)value, written);
        }
    }

    [Fact]
    public void Zero_and_negative_known_amounts_are_compared_by_their_absolute_value()
    {
        AmountGuard.Unknown("Remise de 30,500 TND incluse.", [-30.5m, 0m]).ShouldBeEmpty();
        AmountGuard.Unknown("Remise de 0 TND.", [0m]).ShouldBeEmpty("zero is never an amount of a mention");
    }

    [Fact]
    public void The_millimes_of_a_known_amount_are_not_rounded_away()
    {
        AmountGuard.Unknown("Total : 681,450 TND", [681.400m]).ShouldNotBeEmpty();
        AmountGuard.Unknown("Total : 681 TND", [681.900m]).ShouldBeEmpty("681 is the dinar part of 681.900");
        AmountGuard.Unknown("Total : 682 TND", [681.900m]).ShouldBeEmpty("682 is 681.900 rounded to the dinar");
        AmountGuard.Unknown("Total : 683 TND", [681.900m]).ShouldNotBeEmpty();
    }
}
