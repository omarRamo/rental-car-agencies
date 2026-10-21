namespace CarRental.Domain.Messaging.Agent;

/// <summary>
/// The fixed replies of the WhatsApp agent, sent without the model (or in place of a blocked reply), in the customer's
/// language. They state no price, no availability and no confirmation, never ask for a document photo, and send
/// documents to the secure link of the booking funnel only (invariants 9 and 10).
/// </summary>
public static class AgentMessages
{
    /// <summary>The conversation goes to an agent of the agency (transfer, caps, guardrail, refusal, failures).</summary>
    public static string HandOver(AgentLanguage language) => language switch
    {
        AgentLanguage.English => "Thank you for your message. An agent from our agency will take over this conversation and reply very soon.",
        AgentLanguage.Arabic => "شكراً على رسالتك. سيتولى أحد أعوان الوكالة هذه المحادثة ويرد عليك قريباً.",
        AgentLanguage.Darija => "Ya3tik essa7a 3la message mte3ek. Wa7ed mel agence bech ykammel m3ak w yjewbek 9rib.",
        _ => "Merci pour votre message. Un conseiller de l'agence va reprendre la conversation et vous répondre très bientôt.",
    };

    /// <summary>
    /// A turn made of attachments only: they are not opened; documents go through the secure link of the booking funnel
    /// (<paramref name="secureLink"/>), never through WhatsApp.
    /// </summary>
    public static string AttachmentNotProcessed(AgentLanguage language, string secureLink)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secureLink);
        return language switch
        {
            AgentLanguage.English =>
                $"Thank you! To protect your data, I don't open attachments sent on WhatsApp. To book and upload your documents (ID, driving licence), please use our secure link: {secureLink}. Tell me your dates and pick-up place too, and I'll suggest the cars available.",
            AgentLanguage.Arabic =>
                $"شكراً! حفاظاً على بياناتك، لا أفتح المرفقات المرسلة عبر واتساب. للحجز وإرسال وثائقك (بطاقة التعريف، رخصة السياقة)، استعمل رابطنا الآمن: {secureLink}. أخبرني أيضاً بتواريخك ومكان استلام السيارة لأقترح عليك السيارات المتوفرة.",
            AgentLanguage.Darija =>
                $"Merci ! Bech nhafdhou 3la les données mte3ek, ma nhellech les fichiers elli tab3athhom 3la WhatsApp. Bech ta3mel réservation w tab3ath les papiers (CIN, permis), esta3mel le lien sécurisé : {secureLink}. 9olli zeda les dates w win t7eb tekhou el karhba, nchoufelek el karhabet elli famma.",
            _ =>
                $"Merci ! Pour protéger vos données, je n'ouvre pas les pièces jointes envoyées sur WhatsApp. Pour réserver et déposer vos documents (pièce d'identité, permis), utilisez notre lien sécurisé : {secureLink}. Indiquez-moi aussi vos dates et votre lieu de prise en charge, je vous propose les voitures disponibles.",
        };
    }

    /// <summary>In place of a reply asking for a document photo: documents go through the secure link only.</summary>
    public static string DocumentsOnSecureLinkOnly(AgentLanguage language, string secureLink)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secureLink);
        return language switch
        {
            AgentLanguage.English =>
                $"For your security, documents (ID, driving licence) are never sent on WhatsApp: you upload them on our secure link when you finalise your request: {secureLink}.",
            AgentLanguage.Arabic =>
                $"لأمانك، لا ترسل الوثائق (بطاقة التعريف، رخصة السياقة) عبر واتساب: تُرفع على رابطنا الآمن عند استكمال طلبك: {secureLink}.",
            AgentLanguage.Darija =>
                $"Bech tkoun en sécurité, les papiers (CIN, permis) ma tab3athhomch 3la WhatsApp : t7otthom fil lien sécurisé ki tkammel el demande mte3ek : {secureLink}.",
            _ =>
                $"Pour votre sécurité, les documents (pièce d'identité, permis) ne s'envoient jamais par WhatsApp : vous les déposez sur notre lien sécurisé en finalisant votre demande : {secureLink}.",
        };
    }

    /// <summary>In place of a reply that would disclose the instructions of the assistant: a polite refusal, back to the rental.</summary>
    public static string PoliteRefusal(AgentLanguage language) => language switch
    {
        AgentLanguage.English => "I can't share that. I'm here to help you rent a car: tell me your dates and your pick-up place.",
        AgentLanguage.Arabic => "لا يمكنني مشاركة ذلك. أنا هنا لمساعدتك على كراء سيارة: أخبرني بتواريخك ومكان الاستلام.",
        AgentLanguage.Darija => "Ma najjamch na3tik hedhi. Ena houni bech n3awnek tekri karhba : 9olli les dates w win t7eb tekhou el karhba.",
        _ => "Je ne peux pas partager cela. Je suis là pour vous aider à louer une voiture : indiquez-moi vos dates et votre lieu de prise en charge.",
    };

    /// <summary>All the fixed replies of a language, with a placeholder link (checked by the tests against the guardrails).</summary>
    public static IReadOnlyList<string> All(AgentLanguage language, string secureLink) =>
    [
        HandOver(language),
        AttachmentNotProcessed(language, secureLink),
        DocumentsOnSecureLinkOnly(language, secureLink),
        PoliteRefusal(language),
    ];
}
