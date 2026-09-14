using System.Globalization;
using System.Text;

namespace NetCoreAI.Integration.Tests.Acceptance;

/// <summary>One question and the passage that answers it.</summary>
/// <param name="Question">Asked the way a user would, not the way the document is written.</param>
/// <param name="Document">Title of the document holding the answer.</param>
/// <param name="Page">1-based page the answer is on.</param>
public sealed record QaItem(string Question, string Document, int Page);

/// <summary>
/// The acceptance corpus: ten documents of fifty pages each, and thirty questions whose answers sit on
/// known pages.
/// </summary>
/// <remarks>
/// The corpus is generated rather than committed so the test can state exactly what is in it, and so the
/// answers' locations are known without anyone maintaining a spreadsheet. Every question is deliberately
/// worded differently from the sentence that answers it: matching on shared keywords would measure string
/// overlap rather than retrieval. Filler pages are drawn from each document's own vocabulary, so they are
/// plausible near-misses rather than obvious noise.
/// </remarks>
internal static class Corpus
{
    public const int DocumentCount = 10;

    public const int PagesPerDocument = 50;

    /// <summary>The facts placed in the corpus, with the question each should answer.</summary>
    private static readonly (string Document, int Page, string Fact, string Question)[] Facts =
    [
        ("Field Operations Manual", 7,
            "The auxiliary pump must be primed for ninety seconds before the main valve is opened.",
            "How long does the backup pump need priming before the primary valve opens?"),
        ("Field Operations Manual", 23,
            "Crews working after dark carry two independent light sources and a whistle.",
            "What must night crews bring with them?"),
        ("Field Operations Manual", 41,
            "A spill larger than twenty litres is reported to the regional coordinator within one hour.",
            "Who is told about a large spill, and how quickly?"),

        ("Employee Handbook", 4,
            "Every permanent employee receives twenty-five days of paid annual leave.",
            "How much holiday does a permanent member of staff get?"),
        ("Employee Handbook", 18,
            "Unused leave may be carried into the following year up to a maximum of five days.",
            "Can I roll unused holiday over, and how much?"),
        ("Employee Handbook", 31,
            "Expense claims are submitted within thirty days of the spend or they are refused.",
            "What is the deadline for putting in an expense claim?"),
        ("Employee Handbook", 46,
            "Probation lasts six months and ends with a written review meeting.",
            "How long is the probationary period?"),

        ("Network Security Policy", 9,
            "Administrator accounts require hardware second-factor tokens, not application codes.",
            "What kind of second factor do admin accounts need?"),
        ("Network Security Policy", 27,
            "Suspected phishing messages are forwarded unopened to the security mailbox.",
            "What should someone do with an email they think is phishing?"),
        ("Network Security Policy", 44,
            "Laptops are encrypted at rest and screen-lock after five minutes of inactivity.",
            "After how long does an idle laptop lock itself?"),

        ("Building Maintenance Schedule", 12,
            "Lift inspections take place every six months and are recorded in the building log.",
            "How often are the elevators checked?"),
        ("Building Maintenance Schedule", 33,
            "Roof gutters are cleared each October before the winter rains.",
            "When is gutter clearing done?"),

        ("Customer Support Playbook", 6,
            "A first reply is sent within four working hours of a ticket being raised.",
            "How quickly must support answer a new ticket?"),
        ("Customer Support Playbook", 21,
            "Refunds above five hundred pounds need a team leader's approval before processing.",
            "When does a refund need a manager to sign it off?"),
        ("Customer Support Playbook", 38,
            "Angry customers are offered a call back rather than continuing by email.",
            "What is the advice for dealing with an upset customer over email?"),

        ("Procurement Guidelines", 14,
            "Purchases over ten thousand pounds require three written quotations.",
            "How many quotes are needed for a large purchase?"),
        ("Procurement Guidelines", 29,
            "Contracts with a term beyond three years are reviewed by the legal team.",
            "Which agreements does legal have to look at?"),
        ("Procurement Guidelines", 47,
            "Suppliers are paid thirty days after an undisputed invoice is received.",
            "What are the standard payment terms for vendors?"),

        ("Quality Assurance Standards", 11,
            "A batch failing two consecutive samples is quarantined and re-tested in full.",
            "What happens when a batch fails sampling twice in a row?"),
        ("Quality Assurance Standards", 26,
            "Calibration certificates are retained for seven years after the instrument is retired.",
            "How long are calibration records kept?"),
        ("Quality Assurance Standards", 42,
            "Temperature in the cold store is logged every fifteen minutes by automatic sensors.",
            "How frequently is the chiller temperature recorded?"),

        ("Data Retention Schedule", 8,
            "Payroll records are kept for six years after an employee leaves.",
            "How long do we hold on to payroll information for former staff?"),
        ("Data Retention Schedule", 24,
            "Recruitment files for unsuccessful candidates are deleted after twelve months.",
            "When are applications from people we did not hire destroyed?"),
        ("Data Retention Schedule", 39,
            "Access logs are retained for ninety days unless an investigation is open.",
            "What is the retention period for access logs?"),

        ("Training Curriculum", 16,
            "New starters complete fire safety training in their first week.",
            "What training happens during someone's first days?"),
        ("Training Curriculum", 35,
            "First-aid certification is renewed every three years.",
            "How often does first aid need redoing?"),

        ("Vehicle Fleet Handbook", 5,
            "Tyre pressures are checked before any journey longer than one hundred miles.",
            "When should a driver check the tyres?"),
        ("Vehicle Fleet Handbook", 19,
            "Accidents are reported to the fleet office before the end of the same shift.",
            "How soon must a collision be reported?"),
        ("Vehicle Fleet Handbook", 30,
            "Vehicles are serviced every ten thousand miles or annually, whichever comes first.",
            "What is the service interval for a company van?"),
        ("Vehicle Fleet Handbook", 48,
            "Personal use of a fleet vehicle requires written permission from a line manager.",
            "Can I use a work vehicle privately?"),
    ];

    /// <summary>Vocabulary per document, so filler pages read like the document they are in.</summary>
    private static readonly Dictionary<string, string[]> Topics = new(StringComparer.Ordinal)
    {
        ["Field Operations Manual"] = ["site", "crew", "equipment", "valve", "inspection", "hazard", "shift", "permit"],
        ["Employee Handbook"] = ["employee", "manager", "policy", "absence", "benefit", "review", "conduct", "notice"],
        ["Network Security Policy"] = ["account", "password", "network", "device", "incident", "access", "encryption", "audit"],
        ["Building Maintenance Schedule"] = ["boiler", "lighting", "ventilation", "contractor", "inspection", "repair", "fabric", "alarm"],
        ["Customer Support Playbook"] = ["ticket", "customer", "escalation", "response", "queue", "tone", "resolution", "feedback"],
        ["Procurement Guidelines"] = ["supplier", "quotation", "contract", "invoice", "tender", "budget", "approval", "delivery"],
        ["Quality Assurance Standards"] = ["batch", "sample", "tolerance", "calibration", "defect", "record", "inspection", "standard"],
        ["Data Retention Schedule"] = ["record", "retention", "archive", "deletion", "register", "category", "custodian", "review"],
        ["Training Curriculum"] = ["module", "trainee", "assessment", "competence", "refresher", "workshop", "mentor", "syllabus"],
        ["Vehicle Fleet Handbook"] = ["driver", "vehicle", "fuel", "mileage", "insurance", "licence", "maintenance", "journey"],
    };

    public static IReadOnlyList<string> DocumentTitles => [.. Topics.Keys];

    /// <summary>The questions to ask, in a fixed order so a failure names the same item each run.</summary>
    public static IReadOnlyList<QaItem> Questions =>
        [.. Facts.Select(f => new QaItem(f.Question, f.Document, f.Page))];

    /// <summary>Writes the corpus as PDFs into <paramref name="folder"/>, returning the total page count.</summary>
    public static int Write(string folder)
    {
        Directory.CreateDirectory(folder);
        var pages = 0;

        foreach (var title in DocumentTitles)
        {
            var content = new string[PagesPerDocument];
            for (var page = 1; page <= PagesPerDocument; page++)
            {
                var fact = Facts.FirstOrDefault(f => f.Document == title && f.Page == page);
                content[page - 1] = fact.Fact is not null
                    ? $"{title} - section {page}. {fact.Fact} {Filler(title, page, sentences: 3)}"
                    : $"{title} - section {page}. {Filler(title, page, sentences: 5)}";
            }

            File.WriteAllBytes(Path.Combine(folder, FileName(title)), MinimalPdf.WithPages(content));
            pages += PagesPerDocument;
        }

        return pages;
    }

    public static string FileName(string title) => title.Replace(' ', '-').ToLowerInvariant() + ".pdf";

    /// <summary>
    /// Plausible prose from the document's own vocabulary. Deterministic for a given page, so a run that
    /// fails can be reproduced exactly.
    /// </summary>
    private static string Filler(string title, int page, int sentences)
    {
        var words = Topics[title];
        var random = new Random(HashCode.Combine(title, page));
        var builder = new StringBuilder();

        for (var i = 0; i < sentences; i++)
        {
            var subject = words[random.Next(words.Length)];
            var other = words[random.Next(words.Length)];
            builder
                .Append("The ").Append(subject)
                .Append(" is reviewed by the responsible team and recorded against the ").Append(other)
                .Append(" register in the usual way for period ")
                .Append(page.ToString(CultureInfo.InvariantCulture))
                .Append(". ");
        }

        return builder.ToString().TrimEnd();
    }
}
