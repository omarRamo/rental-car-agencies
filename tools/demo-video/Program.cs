// Records a narrated demo video of the platform running locally: a booking on the public site, its check and validation
// in the back office, then a guided tour of the back office. French captions are drawn on the page and a French voice-over
// (Windows speech synthesis) is mixed in with ffmpeg. Usage and prerequisites: README.md next to this file.
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Speech.Synthesis;
using System.Text.RegularExpressions;
using CarRental.DemoVideo;
using Microsoft.Playwright;

var baseUrl = (args.FirstOrDefault(a => a.StartsWith("http", StringComparison.Ordinal)) ?? "https://localhost:7180").TrimEnd('/');
var outDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "out"));
Directory.CreateDirectory(outDir);
var voiceDir = Path.Combine(outDir, "voice");
Directory.CreateDirectory(voiceDir);

// Demo accounts of the development seed (src/CarRental.Web/appsettings.Development.json).
const string ManagerEmail = "gerant@demo.local";
const string Password = "Demo-Passw0rd-2026";
const string AuthenticatorKey = "CARRENTALDEMOTOTPKEYFORTESTSONLY";

var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Africa/Tunis")).DateTime);
string Local(int days) => today.AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T10:00";
var suffix = Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
// Names take letters only (the funnel refuses digits in a name): a customer drawn at random for each recording.
string[] firstNames = ["Sami", "Leïla", "Yassine", "Amel", "Karim", "Nour", "Mehdi", "Ines"];
string[] lastNames = ["Ben Amor", "Trabelsi", "Jaziri", "Gharbi", "Mansouri", "Hammami", "Khelifi", "Bouzid"];
var firstName = firstNames[Random.Shared.Next(firstNames.Length)];
var lastName = lastNames[Random.Shared.Next(lastNames.Length)];
string? bookingNumber = null;

var steps = new List<Step>();
void AddStep(string chapter, string text, Func<IPage, Task> action) => steps.Add(new Step(chapter, text, action));

// ---------------------------------------------------------------- 1. The public site
AddStep("Vitrine", "Bienvenue dans la démonstration de la plateforme de location de voitures. Nous allons réserver comme un client sur la vitrine, puis traiter la demande dans le back office de l'agence.", async page =>
{
    // The site of the demo agency, at its address (ADR-0067).
    await page.GotoAsync($"{baseUrl}/agence-demo-tunis/fr");
    await Pause(1500);
});

AddStep("Vitrine", "Voici la vitrine de l'agence, en français et en anglais. Le client choisit son lieu de prise en charge et ses dates.", async page =>
{
    var form = page.Locator("form:has(select[name='lieu'])").First;
    await form.ScrollIntoViewIfNeededAsync();
    await Pause(600);
    var place = form.Locator("select[name='lieu']");
    await MoveTo(place);
    await place.SelectOptionAsync(new SelectOptionValue { Value = "TUN" });
    await Pause(700);
    await MoveTo(form.Locator("input[name='depart']"));
    await form.Locator("input[name='depart']").FillAsync(Local(21));
    await Pause(700);
    await MoveTo(form.Locator("input[name='retour']"));
    await form.Locator("input[name='retour']").FillAsync(Local(25));
    await Pause(900);
    await Click(form.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Rechercher" }));
});

AddStep("Vitrine", "Les disponibilités et les prix viennent en temps réel du moteur de l'agence : chaque catégorie affiche son prix total toutes taxes comprises et sa caution, sans aucun risque de surréservation.", async page =>
{
    await page.Locator("li.offer").First.WaitForAsync();
    await Pause(1200);
    await Scroll(page, 380);
    await Pause(1500);
    await Scroll(page, 380);
    await Pause(1200);
});

AddStep("Réservation", "Le client choisit une catégorie, ici une économique. Étape un : les options. Ajoutons un siège enfant : le prix se met à jour aussitôt.", async page =>
{
    var offer = page.Locator("li.offer:has(.offer__code:text-is('ECMR'))");
    await offer.ScrollIntoViewIfNeededAsync();
    await Pause(800);
    await Click(offer.Locator("form.offer__choose button"));
    await page.WaitForURLAsync(new Regex("/options$"));
    await Pause(1200);
    await MoveTo(page.GetByLabel("Siège enfant"));
    await page.GetByLabel("Siège enfant").SelectOptionAsync("1");
    await Pause(600);
    await Click(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Mettre à jour le prix" }));
    await Pause(1500);
    await page.Locator("[data-testid='funnel-total']").ScrollIntoViewIfNeededAsync();
    await Pause(1200);
});

AddStep("Réservation", "Étape deux : le conducteur saisit son identité et son permis. Les photos des pièces peuvent être déposées maintenant ou plus tard, depuis l'espace Ma réservation.", async page =>
{
    await Click(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Continuer vers les conducteurs" }));
    await page.WaitForURLAsync(new Regex("/conducteurs$"));
    await Pause(800);
    await Type(page.Locator("#main-first-name"), firstName);
    await Type(page.Locator("#main-last-name"), lastName);
    await page.Locator("#main-nationality").SelectOptionAsync("FR");
    await page.Locator("#main-birth-date").FillAsync("1985-04-12");
    await Type(page.Locator("#main-phone"), $"+3361{Random.Shared.Next(1_000_000, 9_999_999)}");
    await Type(page.Locator("#main-email"), $"client.{suffix.ToLowerInvariant()}@example.com");
    await Type(page.Locator("#main-license-number"), $"PERMIS{suffix}");
    await page.Locator("#main-license-country").SelectOptionAsync("FR");
    await page.Locator("#main-license-issued-on").FillAsync("2005-06-01");
    await Type(page.Locator("#main-identity-number"), $"PASS{suffix}");
    await page.Locator("#main-identity-expires-on").FillAsync("2034-01-31");
    await page.Locator("#consent-documents").CheckAsync();
    await Pause(900);
});

AddStep("Réservation", "Étape trois : il accepte les conditions générales et dépose une garantie par carte, sur la page sécurisée de la banque, ici simulée. L'agence ne voit jamais le numéro de carte.", async page =>
{
    await Click(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Continuer vers le paiement" }));
    await page.WaitForURLAsync(new Regex("/paiement$"));
    await Pause(1500);
    await Scroll(page, 300);
    await Pause(1000);
    await page.GetByLabel("J'ai lu et j'accepte les conditions générales de location").CheckAsync();
    await WaitForTurnstileAsync(page);
    await Pause(800);
    await Click(page.Locator("[data-testid='funnel-submit']"));
    await page.WaitForURLAsync(new Regex("/dev/clictopay/pay/"));
    await Pause(1800);
    await Click(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Payer" }));
});

AddStep("Réservation", "La demande est reçue et la garantie est bloquée sur la carte. Le client reçoit un e-mail avec son numéro de réservation et un lien vers Ma réservation.", async page =>
{
    await page.WaitForURLAsync(new Regex("/confirmation/LOC-[0-9]{4}-[0-9]{6}$"));
    bookingNumber = Regex.Match(page.Url, "LOC-[0-9]{4}-[0-9]{6}$").Value;
    await Chip(page, $"Demande {bookingNumber}");
    await Pause(2500);
});

// ---------------------------------------------------------------- 2. The back office
AddStep("Back office", "Côté agence, un membre de l'équipe se connecte au back office ; en production, la double authentification est obligatoire pour tout le personnel.", async page =>
{
    await page.GotoAsync($"{baseUrl}/account/login?ReturnUrl=%2Fadmin");
    await Pause(800);
    await Type(page.Locator("#email"), ManagerEmail);
    await Type(page.Locator("#password"), Password, delay: 40);
    await Click(page.Locator("button[type=submit]"));

    // The code page comes only when the second factor is on (it is off on a developer's machine).
    var code = page.Locator("#code");
    await page.WaitForFunctionAsync("() => location.pathname === '/admin' || document.querySelector('#code') !== null");
    if (await code.CountAsync() > 0)
    {
        await Pause(700);
        await Type(code, Totp.Compute(AuthenticatorKey), delay: 120);
        await Click(page.Locator("button[type=submit]"));
    }

    await page.WaitForURLAsync(new Regex("/admin$"));
});

AddStep("Back office", "Le tableau du jour résume les départs et les retours, les demandes à valider, les retards et les alertes de la flotte.", async page =>
{
    await WaitInteractiveAsync(page);
    await Pause(1500);
    await Scroll(page, 450);
    await Pause(1500);
    await Scroll(page, -450);
});

AddStep("Back office", "La nouvelle demande arrive dans la file des demandes web. L'agent ouvre son contrôle : disponibilité, âge et ancienneté du permis, contrôle client et garantie bancaire sont vérifiés automatiquement ; les pièces seront contrôlées au comptoir.", async page =>
{
    await Nav(page, "Demandes web");
    var item = page.Locator($"li[data-request='{bookingNumber}']");
    await item.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
    await Pause(900);
    await Click(item.GetByRole(AriaRole.Button));
    await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = $"Contrôle de la demande {bookingNumber}" }).WaitForAsync();
    await Pause(1800);
});

AddStep("Back office", "L'agent valide : la garantie est encaissée, la réservation est confirmée et quitte la file ; le client en est informé par e-mail.", async page =>
{
    await Click(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Valider", Exact = true }));
    var dialog = page.GetByRole(AriaRole.Dialog);
    await dialog.WaitForAsync();
    await Pause(1400);
    await Click(dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Valider", Exact = true }));
    await page.GetByText("Demande validée : réservation confirmée.").WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
    await Pause(1500);
});

AddStep("Back office", "Le dossier garde toute son histoire : devis figé, paiements, caution, documents et une chronologie de chaque action.", async page =>
{
    await Nav(page, "Dossiers");
    await page.GetByLabel("Rechercher").FillAsync(bookingNumber!);
    var link = page.Locator("table a[href^='/admin/bookings/']").First;
    await link.WaitForAsync();
    await Pause(900);
    await Click(link);
    await page.WaitForURLAsync(new Regex("/admin/bookings/[0-9a-f-]{36}$"));
    await WaitInteractiveAsync(page);
    await Pause(1600);
    await Scroll(page, 500);
    await Pause(1400);
    await Scroll(page, 500);
    await Pause(1000);
});

AddStep("Back office", "Le planning montre chaque réservation par catégorie et par véhicule. L'agent attribue un véhicule, ou laisse le système proposer les attributions sans conflit.", async page =>
{
    await Nav(page, "Planning");
    await Pause(2200);
    await Scroll(page, 350);
    await Pause(1500);
});

// ---------------------------------------------------------------- 3. Tour of the back office
AddStep("Exploitation", "Les départs et les retours se font sur tablette : état des lieux avec photos, signatures, contrat PDF, puis frais de retour calculés par le moteur.", page => Visit(page, "Départs et retours"));
AddStep("Exploitation", "La fiche client réunit les conducteurs, leurs pièces d'identité, dont chaque consultation est tracée, et leurs consentements.", page => Visit(page, "Clients et conducteurs"));
AddStep("Exploitation", "La flotte suit chaque véhicule : documents et échéances, entretien, kilométrage et rentabilité.", page => Visit(page, "Flotte et échéances"));
AddStep("Commercial", "Les tarifs se gèrent par saison, par durée et par canal de vente, avec des plans publiés et un simulateur de prix.", page => Visit(page, "Tarifs et saisons"));
AddStep("Commercial", "La boîte WhatsApp est partagée par l'équipe : l'assistant IA répond aux clients avec les vrais prix, et un agent peut reprendre la main à tout moment.", page => Visit(page, "Boîte WhatsApp"));
AddStep("Commercial", "Les canaux de vente, comme Booking.com, partagent la même disponibilité, avec des quotas et des arrêts de vente par canal.", page => Visit(page, "Canaux de vente"));
AddStep("Finance", "Paiements et cautions : Clic to Pay, terminal, espèces, et cautions suivies comme une dette envers le client.", page => Visit(page, "Paiements et cautions"));
AddStep("Finance", "Les factures et les avoirs sont numérotés sans trou, avec la TVA et le timbre ; la facture électronique est prête pour Tunisie TradeNet.", page => Visit(page, "Factures et avoirs"));
AddStep("Finance", "La caisse de chaque agent, puis les dépenses, les rapprochements et les exports comptables avec la clôture mensuelle.", async page =>
{
    await Visit(page, "Caisse");
    await Visit(page, "Exports comptables");
});
AddStep("Finance", "Les indicateurs pilotent l'activité : taux d'utilisation, revenu par véhicule, conversion du site et de l'assistant WhatsApp.", page => Visit(page, "Indicateurs"));
AddStep("Administration", "Enfin, l'administration : utilisateurs et rôles, paramètres de l'agence, et un journal d'audit de chaque action sensible.", async page =>
{
    await Visit(page, "Utilisateurs et rôles");
    await Visit(page, "Journal d'audit");
});
AddStep("Fin", "Fin de la démonstration. Merci de votre attention.", async page =>
{
    await Nav(page, "Tableau du jour");
    await Pause(2500);
});

// ---------------------------------------------------------------- Narration, recording, mixing
Console.WriteLine($"Synthesizing {steps.Count} narration segments…");
using (var synthesizer = new SpeechSynthesizer())
{
    var voice = synthesizer.GetInstalledVoices().Select(v => v.VoiceInfo).FirstOrDefault(v => v.Culture.Name == "fr-FR")
                ?? throw new InvalidOperationException("No French (fr-FR) voice is installed for Windows speech synthesis.");
    synthesizer.SelectVoice(voice.Name);
    synthesizer.Rate = 1;
    for (var i = 0; i < steps.Count; i++)
    {
        var wav = Path.Combine(voiceDir, $"{i:D2}.wav");
        synthesizer.SetOutputToWaveFile(wav);
        synthesizer.Speak(steps[i].Text);
        synthesizer.SetOutputToNull();
        steps[i] = steps[i] with { Wav = wav, Duration = Wav.Duration(wav) };
    }
}

using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
var videoDir = Path.Combine(outDir, "raw");
Directory.CreateDirectory(videoDir);
var context = await browser.NewContextAsync(new BrowserNewContextOptions
{
    ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
    RecordVideoDir = videoDir,
    RecordVideoSize = new RecordVideoSize { Width = 1440, Height = 900 },
    IgnoreHTTPSErrors = true,
    BypassCSP = true, // the overlay (captions, cursor) is injected into pages that forbid inline styles
    Locale = "fr-FR",
    TimezoneId = "Africa/Tunis",
});
context.SetDefaultTimeout(30_000);
await context.AddInitScriptAsync(Overlay.Script);
var pageUnderTest = await context.NewPageAsync();
var clock = Stopwatch.StartNew();

var cues = new List<(TimeSpan At, Step Step)>();
var next = TimeSpan.Zero;
foreach (var step in steps)
{
    if (clock.Elapsed < next)
    {
        await Task.Delay(next - clock.Elapsed);
    }

    var at = clock.Elapsed;
    cues.Add((at, step));
    await Caption(pageUnderTest, step.Text);
    await Chapter(pageUnderTest, step.Chapter);
    next = at + step.Duration + TimeSpan.FromMilliseconds(700);
    Console.WriteLine($"{at:mm\\:ss} [{step.Chapter}] {step.Text[..Math.Min(70, step.Text.Length)]}…");
    try
    {
        await step.Action(pageUnderTest);
    }
    catch
    {
        // What the page showed when the step failed, to fix the scenario.
        await pageUnderTest.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(outDir, "failure.png"), FullPage = true });
        Console.WriteLine($"Failed on {pageUnderTest.Url}");
        Console.WriteLine((await pageUnderTest.Locator("body").InnerTextAsync())[..Math.Min(1500, (await pageUnderTest.Locator("body").InnerTextAsync()).Length)]);
        throw;
    }
}

if (clock.Elapsed < next)
{
    await Task.Delay(next - clock.Elapsed);
}

await Task.Delay(1500);
var rawVideo = await pageUnderTest.Video!.PathAsync();
await context.CloseAsync();

var output = Path.Combine(outDir, "demo-carrental.mp4");
Mixer.Run(rawVideo, cues.Select(c => (c.At, c.Step.Wav!)).ToList(), output);
await File.WriteAllTextAsync(Path.Combine(outDir, "demo-carrental.srt"), Subtitles.Srt(cues.Select(c => (c.At, c.Step.Duration, c.Step.Text)).ToList()));
Console.WriteLine($"Video: {output}");
return;

// ---------------------------------------------------------------- Helpers
static Task Pause(int milliseconds) => Task.Delay(milliseconds);

static async Task Type(ILocator field, string text, int delay = 55)
{
    await MoveTo(field);
    await field.ClickAsync();
    await field.FillAsync(string.Empty);
    await field.PressSequentiallyAsync(text, new LocatorPressSequentiallyOptions { Delay = delay });
}

static async Task Scroll(IPage page, int pixels)
{
    // Over the content, not the menu, which scrolls on its own.
    await page.Mouse.MoveAsync(960, 470, new MouseMoveOptions { Steps = 12 });
    for (var done = 0; Math.Abs(done) < Math.Abs(pixels); done += Math.Sign(pixels) * 40)
    {
        await page.Mouse.WheelAsync(0, Math.Sign(pixels) * 40);
        await Task.Delay(16);
    }

    await Task.Delay(300);
}

static async Task MoveTo(ILocator target)
{
    await target.ScrollIntoViewIfNeededAsync();
    var box = await target.BoundingBoxAsync();
    if (box is not null)
    {
        await target.Page.Mouse.MoveAsync(box.X + (box.Width / 2), box.Y + (box.Height / 2), new MouseMoveOptions { Steps = 25 });
    }

    await Task.Delay(250);
}

static async Task Click(ILocator target)
{
    await MoveTo(target);
    await target.ClickAsync();
}

static Task Caption(IPage page, string text) => page.EvaluateAsync("t => window.__demoCaption && window.__demoCaption(t)", text);

static Task Chapter(IPage page, string text) => page.EvaluateAsync("t => window.__demoChapter && window.__demoChapter(t)", text);

static Task Chip(IPage page, string text) => page.EvaluateAsync("t => window.__demoChip && window.__demoChip(t)", text);

static async Task WaitInteractiveAsync(IPage page) =>
    await page.Locator("main#admin-main[data-interactive='true']").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached });

static async Task Nav(IPage page, string label)
{
    var link = page.Locator("nav").GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = label, Exact = true }).First;
    await MoveTo(link);
    await Task.Delay(500);
    await Click(link);
    await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
    await WaitInteractiveAsync(page);
}

static async Task Visit(IPage page, string label)
{
    await Nav(page, label);
    await Task.Delay(1800);
    await Scroll(page, 400);
    await Task.Delay(1300);
    await Scroll(page, -400);
}

static async Task WaitForTurnstileAsync(IPage page)
{
    // The development site key of Cloudflare Turnstile always passes; its token lands in a hidden field.
    var field = page.Locator("input[name='cf-turnstile-response']");
    if (await field.CountAsync() == 0)
    {
        return;
    }

    for (var i = 0; i < 60 && string.IsNullOrEmpty(await field.First.InputValueAsync()); i++)
    {
        await Task.Delay(500);
    }
}

namespace CarRental.DemoVideo
{
    internal sealed record Step(string Chapter, string Text, Func<IPage, Task> Action)
    {
        public string? Wav { get; init; }

        public TimeSpan Duration { get; init; }
    }

    /// <summary>Captions, chapter chip and a visible mouse pointer, drawn by the page itself (re-installed after every navigation).</summary>
    internal static class Overlay
    {
        public const string Script = """
            (() => {
              if (window.__demoInstalled) return;
              window.__demoInstalled = true;
              const install = () => {
                const style = document.createElement('style');
                style.textContent = `
                  #demo-caption { position: fixed; left: 50%; bottom: 26px; transform: translateX(-50%); max-width: 80%;
                    background: rgba(15, 23, 42, .9); color: #fff; font: 600 21px/1.45 "Segoe UI", system-ui, sans-serif;
                    padding: 14px 26px; border-radius: 14px; z-index: 2147483647; text-align: center;
                    box-shadow: 0 10px 30px rgba(0, 0, 0, .35); pointer-events: none; }
                  #demo-caption:empty { display: none; }
                  #demo-chapter { position: fixed; top: 14px; left: 50%; transform: translateX(-50%); background: #0b57d0; color: #fff;
                    font: 700 14px/1 "Segoe UI", system-ui, sans-serif; letter-spacing: .06em; text-transform: uppercase;
                    padding: 9px 16px; border-radius: 999px; z-index: 2147483647; pointer-events: none; box-shadow: 0 4px 14px rgba(0,0,0,.25); }
                  #demo-chapter:empty, #demo-chip:empty { display: none; }
                  #demo-chip { position: fixed; top: 14px; right: 18px; background: #15803d; color: #fff;
                    font: 700 14px/1 "Segoe UI", system-ui, sans-serif; padding: 9px 14px; border-radius: 999px;
                    z-index: 2147483647; pointer-events: none; }
                  #demo-cursor { position: fixed; width: 24px; height: 24px; margin: -12px 0 0 -12px; border-radius: 50%;
                    background: rgba(239, 68, 68, .28); border: 2px solid rgba(220, 38, 38, .95); z-index: 2147483647;
                    pointer-events: none; transition: transform .12s, background .12s; left: -100px; top: -100px; }
                  #demo-cursor.down { transform: scale(.7); background: rgba(220, 38, 38, .65); }`;
                document.documentElement.appendChild(style);
                const make = (id, key) => {
                  const element = document.createElement('div');
                  element.id = id;
                  element.textContent = sessionStorage.getItem(key) || '';
                  document.documentElement.appendChild(element);
                  return element;
                };
                const caption = make('demo-caption', 'demo-caption');
                const chapter = make('demo-chapter', 'demo-chapter');
                const chip = make('demo-chip', 'demo-chip');
                const cursor = make('demo-cursor', 'demo-cursor-none');
                const position = JSON.parse(sessionStorage.getItem('demo-cursor') || 'null');
                if (position) { cursor.style.left = position.x + 'px'; cursor.style.top = position.y + 'px'; }
                addEventListener('mousemove', e => {
                  cursor.style.left = e.clientX + 'px';
                  cursor.style.top = e.clientY + 'px';
                  sessionStorage.setItem('demo-cursor', JSON.stringify({ x: e.clientX, y: e.clientY }));
                }, true);
                addEventListener('mousedown', () => cursor.classList.add('down'), true);
                addEventListener('mouseup', () => cursor.classList.remove('down'), true);
                const setter = (element, key) => text => { element.textContent = text; sessionStorage.setItem(key, text); };
                window.__demoCaption = setter(caption, 'demo-caption');
                window.__demoChapter = setter(chapter, 'demo-chapter');
                window.__demoChip = setter(chip, 'demo-chip');
              };
              if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', install); else install();
            })();
            """;
    }

    /// <summary>RFC 6238 TOTP (HMAC-SHA1, 30-second steps, 6 digits), the algorithm of ASP.NET Core Identity's authenticator.</summary>
    internal static class Totp
    {
        public static string Compute(string base32Key)
        {
            var key = Base32(base32Key);
            var counter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
            var message = BitConverter.GetBytes(counter);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(message);
            }

            var hash = HMACSHA1.HashData(key, message);
            var offset = hash[^1] & 0x0F;
            var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
            return (binary % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        }

        private static byte[] Base32(string input)
        {
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
            var bits = string.Concat(input.TrimEnd('=').ToUpperInvariant().Select(c => Convert.ToString(alphabet.IndexOf(c), 2).PadLeft(5, '0')));
            return [.. Enumerable.Range(0, bits.Length / 8).Select(i => Convert.ToByte(bits.Substring(i * 8, 8), 2))];
        }
    }

    internal static class Wav
    {
        /// <summary>Duration of a PCM WAV file from its header (byte rate) and data size.</summary>
        public static TimeSpan Duration(string path)
        {
            var bytes = File.ReadAllBytes(path);
            var byteRate = BitConverter.ToInt32(bytes, 28);
            var position = 12;
            while (position + 8 <= bytes.Length)
            {
                var id = System.Text.Encoding.ASCII.GetString(bytes, position, 4);
                var size = BitConverter.ToInt32(bytes, position + 4);
                if (id == "data")
                {
                    return TimeSpan.FromSeconds((double)size / byteRate);
                }

                position += 8 + size;
            }

            throw new InvalidDataException($"No data chunk in {path}.");
        }
    }

    internal static class Mixer
    {
        /// <summary>Encodes the recording to H.264 and mixes in each narration segment at the instant its step started.</summary>
        public static void Run(string video, IReadOnlyList<(TimeSpan At, string Wav)> narration, string output)
        {
            var arguments = new List<string> { "-y", "-i", video };
            foreach (var (_, wav) in narration)
            {
                arguments.AddRange(["-i", wav]);
            }

            var filters = new List<string>();
            for (var i = 0; i < narration.Count; i++)
            {
                var delay = (long)narration[i].At.TotalMilliseconds;
                filters.Add($"[{i + 1}:a]aresample=48000,adelay={delay}|{delay}[a{i}]");
            }

            filters.Add($"{string.Concat(Enumerable.Range(0, narration.Count).Select(i => $"[a{i}]"))}amix=inputs={narration.Count}:normalize=0,apad[voice]");
            arguments.AddRange([
                "-filter_complex", string.Join(';', filters),
                "-map", "0:v", "-map", "[voice]",
                "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p", "-r", "30",
                "-c:a", "aac", "-b:a", "160k", "-shortest", "-movflags", "+faststart",
                output,
            ]);

            var start = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start)!;
            var log = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException("ffmpeg failed:\n" + log[^Math.Min(3000, log.Length)..]);
            }
        }
    }

    internal static class Subtitles
    {
        public static string Srt(IReadOnlyList<(TimeSpan At, TimeSpan Duration, string Text)> cues)
        {
            var builder = new System.Text.StringBuilder();
            for (var i = 0; i < cues.Count; i++)
            {
                var (at, duration, text) = cues[i];
                var end = i + 1 < cues.Count ? cues[i + 1].At : at + duration;
                builder.Append(i + 1).Append('\n')
                    .Append(Time(at)).Append(" --> ").Append(Time(end)).Append('\n')
                    .Append(text).Append("\n\n");
            }

            return builder.ToString();
        }

        private static string Time(TimeSpan value) => value.ToString(@"hh\:mm\:ss\,fff", CultureInfo.InvariantCulture);
    }
}
