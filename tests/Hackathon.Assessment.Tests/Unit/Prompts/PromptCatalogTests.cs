using System.Text.RegularExpressions;
using Hackathon.Assessment.Api.Agents;
using Hackathon.Assessment.Api.Domain;
using Xunit;

namespace Hackathon.Assessment.Tests.Unit.Prompts;

public sealed partial class PromptCatalogTests
{
    private static readonly string[] RequiredHeadings =
    [
        "## Amaç",
        "## Kontrol kapsamı",
        "## Kabul kriterleri",
        "## Tipik ihlaller",
        "## Meşru istisnalar",
        "## Kabul edilen kanıt",
        "## Belirsizlik ve yetersiz kanıt",
        "## Kesin / potansiyel",
        "## Metriğe özel davranış"
    ];

    [Fact]
    public void LoadsTenPromptRubricPairsWithExactK2Content()
    {
        var catalog = new PromptCatalog(PromptRoot);

        foreach (var expected in ExpectedMetrics)
        {
            var metric = catalog.GetMetric(expected.Id);

            Assert.Equal(expected.Id, metric.MetricId);
            Assert.Equal(expected.MetricCode, metric.Rubric.Id);
            Assert.Equal(expected.Name, metric.Rubric.Name);
            Assert.Equal(expected.SourceRef, metric.Rubric.SourceRef);
            Assert.Equal(expected.ControlAtoms, metric.Rubric.SubChecks.Select(static check => check.Title));
            Assert.Equal(
                Enumerable.Range(1, expected.ControlAtoms.Length)
                    .Select(index => $"{expected.MetricCode}-sc{index:00}"),
                metric.Rubric.SubChecks.Select(static check => check.Id));
            Assert.Equal(expected.AcceptanceCriteria, metric.Rubric.AcceptanceCriteria);
            Assert.Equal(
                new[] { "critical", "high", "medium", "low", "info" },
                metric.Rubric.SeverityGuide.Keys);

            Assert.StartsWith($"# {expected.MetricCode} — {expected.Name}", metric.Prompt);
            foreach (var atom in expected.ControlAtoms)
            {
                Assert.Contains($"- {atom}", metric.Prompt, StringComparison.Ordinal);
            }

            foreach (var criterion in expected.AcceptanceCriteria)
            {
                Assert.Contains($"- {criterion}", metric.Prompt, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void EveryControlAtomAndAcceptanceSentenceMatchesBindingSpec()
    {
        var root = RepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "docs/spec/09-metrikler.md"));
        var documentation = File.ReadAllText(Path.Combine(root, "docs/prompts.md"));
        var catalog = new PromptCatalog(PromptRoot);
        var scopeMatches = Regex.Matches(
            source,
            @"^- \*\*m(?<number>[0-9]{2}) \(§6\.[0-9]+\):\*\* (?<scope>.+)$",
            RegexOptions.Multiline);
        var acceptanceMatches = Regex.Matches(
            source,
            @"^- \*\*m(?<number>[0-9]{2}):\*\* (?<criteria>.+)$",
            RegexOptions.Multiline);
        Assert.Equal(10, scopeMatches.Count);
        Assert.Equal(10, acceptanceMatches.Count);

        foreach (var metricId in MetricNames.All)
        {
            var code = $"m{(int)metricId + 1:00}";
            var scope = Assert.Single(scopeMatches.Cast<Match>(),
                match => match.Groups["number"].Value == code[1..]);
            var criteria = Assert.Single(acceptanceMatches.Cast<Match>(),
                match => match.Groups["number"].Value == code[1..]);
            var atoms = scope.Groups["scope"].Value.TrimEnd('.').Split(", ");
            var sentences = criteria.Groups["criteria"].Value.Split(" · ")
                .SelectMany(sentence =>
                    sentence.Contains(". Owner/destek", StringComparison.Ordinal)
                        ? sentence.Split(". ", 2).Select((text, index) =>
                            index == 0 ? text + "." : text)
                        : [sentence])
                .ToArray();
            var metric = catalog.GetMetric(metricId);
            Assert.Equal(atoms, metric.Rubric.SubChecks.Select(item => item.Title));
            Assert.Equal(sentences, metric.Rubric.AcceptanceCriteria);
            Assert.All(sentences, sentence =>
                Assert.Contains($"- {sentence}", metric.Prompt, StringComparison.Ordinal));
            Assert.Contains(
                $"| {code} | {metric.Rubric.Name} | {metric.Rubric.SourceRef} | {atoms.Length} | {sentences.Length} |",
                documentation,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void MetricPromptsHaveRequiredHeadingOrderAndSize()
    {
        var catalog = new PromptCatalog(PromptRoot);

        foreach (var metricId in MetricNames.All)
        {
            var prompt = catalog.GetMetric(metricId).Prompt;
            Assert.InRange(prompt.Length, 1, 6000);

            var previous = -1;
            foreach (var heading in RequiredHeadings)
            {
                var current = prompt.IndexOf(heading, StringComparison.Ordinal);
                Assert.True(current > previous, $"{metricId}: heading '{heading}' is absent or out of order.");
                previous = current;
            }
        }

        Assert.InRange(catalog.ProfilerSystemPrompt.Length, 1, 2500);
        Assert.InRange(catalog.EvaluatorSystemPrompt.Length, 1, 5000);
        Assert.Contains("Report Synthesizer", catalog.SynthesizerSystemPrompt);
        Assert.InRange(catalog.RouterSourcePrompt.Length, 1, 2500);
        Assert.InRange(
            File.ReadAllText(Path.Combine(PromptRoot, "system", "synthesizer.md")).Length,
            1,
            4000);
        Assert.Contains("Safety Policy", catalog.RouterSystemPrompt, StringComparison.Ordinal);
        Assert.Contains("Safety Policy", catalog.SynthesizerSystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void VersionIsDeterministicAndChangesWithContent()
    {
        var first = new PromptCatalog(PromptRoot);
        var second = new PromptCatalog(PromptRoot);

        Assert.Equal(first.PromptVersion, second.PromptVersion);
        Assert.Matches(VersionPattern(), first.PromptVersion);

        WithCatalogCopy(root =>
        {
            File.AppendAllText(Path.Combine(root, "system", "evaluator.md"), "\nAnlamsal değişiklik.\n");
            var changed = new PromptCatalog(root);
            Assert.NotEqual(first.PromptVersion, changed.PromptVersion);
        });
    }

    [Fact]
    public void VersionIncludesAdditionalSystemPromptsWithoutReadingThemAsMetrics()
    {
        WithCatalogCopy(root =>
        {
            var before = new PromptCatalog(root);
            File.WriteAllText(Path.Combine(root, "system", "router.md"), "Sabit router politikası.");
            var after = new PromptCatalog(root);

            Assert.NotEqual(before.PromptVersion, after.PromptVersion);
            Assert.Equal(before.GetMetric(MetricId.M01).Rubric.Id,
                after.GetMetric(MetricId.M01).Rubric.Id);
        });
    }

    [Fact]
    public void MissingRequiredFileFailsCatalogConstruction()
    {
        WithCatalogCopy(root =>
        {
            File.Delete(Path.Combine(root, "metrics", "m10.md"));
            var exception = Assert.Throws<FileNotFoundException>(() => new PromptCatalog(root));
            Assert.Contains("metrics/m10.md", exception.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void BrokenRubricFailsCatalogConstruction()
    {
        WithCatalogCopy(root =>
        {
            File.WriteAllText(Path.Combine(root, "rubrics", "m01.yaml"), "metricId: [");
            var exception = Assert.Throws<InvalidDataException>(() => new PromptCatalog(root));
            Assert.Contains("rubrics/m01.yaml", exception.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void NonCodePolicyIsDeclaredAsNotCodeVerifiable()
    {
        var catalog = new PromptCatalog(PromptRoot);
        var branchPolicy = catalog.GetMetric(MetricId.M07).Rubric.SubChecks.Single(
            static check => check.Title == "branch ve PR politikalarına ilişkin mevcut kanıtlar");

        Assert.False(branchPolicy.CodeVerifiable);
    }

    private static string PromptRoot => Path.Combine(AppContext.BaseDirectory, "prompts");

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Cannot locate repository root.");
    }

    private static IReadOnlyList<ExpectedMetric> ExpectedMetrics { get; } =
    [
        new(
            MetricId.M01,
            "m01",
            "Kod ve Proje Yapısı Standartları",
            "UseCase §6.1",
            [
                "Solution ve proje yapısı", "katman ayrımı", "dependency yönleri",
                "circular dependency", "isimlendirme standartları", "ortak bileşen kullanımı",
                "business logic konumlandırması",
                "controller sorumlulukları ve dependency injection kullanımı"
            ],
            [
                "mimari ihlali doğru tespit etmeli",
                "ilgili dosya/proje/metot bilgisini göstermeli",
                "ihlal edilen Holding standardını belirtmeli",
                "uygulanabilir refactoring önerisi sunmalı."
            ]),
        new(
            MetricId.M02,
            "m02",
            "Kimlik Doğrulama ve Yetkilendirme",
            "UseCase §6.2",
            [
                "Microsoft Entra ID veya kurumsal kimlik altyapısı", "authentication yapılandırması",
                "endpoint bazlı authorization", "rol/claim/permission kontrolleri",
                "anonymous endpointler", "token validation",
                "401/403 kullanımı ve service-to-service kimlik doğrulaması"
            ],
            [
                "korumasız endpointleri tespit etmeli",
                "endpoint veya metot seviyesinde kanıt göstermeli",
                "authentication ile authorization farkını doğru değerlendirmeli",
                "yetkisiz erişim etkisini açıklamalı."
            ]),
        new(
            MetricId.M03,
            "m03",
            "Uygulama Güvenliği ve Secret Yönetimi",
            "UseCase §6.3",
            [
                "Hardcoded secret", "API key", "connection string", "Key Vault kullanımı",
                "input validation", "SQL Injection", "CORS ayarları", "güvenlik headerları",
                "hassas veri loglama ve güvensiz şifreleme yöntemleri"
            ],
            [
                "güvenlik açığını doğru sınıflandırmalı",
                "secret değerlerini raporda açık göstermemeli",
                "kanıtı dosya/satır bilgisiyle sunmalı",
                "güvenli alternatif önermeli",
                "kritik bulguları yüksek öncelikle raporlamalı."
            ]),
        new(
            MetricId.M04,
            "m04",
            "Veri Yönetimi ve Entegrasyon Standartları",
            "UseCase §6.4",
            [
                "Veritabanı erişim standartları", "migration yönetimi", "transaction kullanımı",
                "timeout ve retry politikaları", "API istemci yönetimi",
                "kişisel/hassas veri kullanımı",
                "harici servis bağımlılıkları ve API versioning"
            ],
            [
                "veri tutarlılığı veya entegrasyon dayanıklılığı etkisini açıklamalı",
                "ilgili kod bloğunu göstermeli",
                "genel öneri yerine teknik çözüm sunmalı",
                "retry önerirken idempotency riskini dikkate almalı."
            ]),
        new(
            MetricId.M05,
            "m05",
            "Loglama, İzlenebilirlik ve APM",
            "UseCase §6.5",
            [
                "Structured logging", "Application Insights entegrasyonu", "correlation ID",
                "distributed tracing", "merkezi exception handling", "log seviyeleri",
                "hassas veri maskeleme", "dependency tracking", "health ve telemetry yapılandırması"
            ],
            [
                "observability eksikliğini doğru tespit etmeli",
                "loglama ile monitoring kavramlarını karıştırmamalı",
                "hassas veri loglarını güvenlik riskiyle ilişkilendirmeli",
                "kurum standardına uygun çözüm önermeli."
            ]),
        new(
            MetricId.M06,
            "m06",
            "Test ve Kod Kalitesi Standartları",
            "UseCase §6.6",
            [
                "Unit ve integration testlerin varlığı", "kritik iş kurallarının test edilmesi",
                "test projesi yapısı", "mock kullanımı", "SonarQube yapılandırması",
                "code coverage", "cyclomatic complexity",
                "duplicate code ve testlerin pipeline içinde çalıştırılması"
            ],
            [
                "yalnızca test dosyası sayısına bakmamalı",
                "assertion kalitesini değerlendirmeli",
                "kritik akışların test edilip edilmediğini değerlendirmeli",
                "quality gate durumunu değerlendirmeli",
                "test piramidine uygun iyileştirme ihtiyacını değerlendirmeli."
            ]),
        new(
            MetricId.M07,
            "m07",
            "CI/CD ve Kaynak Kod Yönetimi",
            "UseCase §6.7",
            [
                "Pipeline dosyaları", "build ve test adımları", "code quality gate",
                "security/dependency scanning", "artifact yönetimi", "ortam bazlı deployment",
                "production onayı", "secret kullanımı",
                "branch ve PR politikalarına ilişkin mevcut kanıtlar"
            ],
            [
                "pipeline dosyalarını analiz etmeli",
                "eksik aşamaları doğru sıralamayla açıklamalı",
                "kaynak koddan doğrulanamayacak branch policy konularında varsayım yapmamalı",
                "kanıt yoksa `Değerlendirilemedi` statüsünü kullanmalı."
            ]),
        new(
            MetricId.M08,
            "m08",
            "Container ve Çalışma Ortamı Standartları",
            "UseCase §6.8",
            [
                "Dockerfile varlığı", "onaylı base image", "multi-stage build",
                "non-root çalışma", "image boyutu", "port ve health check",
                "environment variable kullanımı", "secret yönetimi",
                "CPU/memory limitleri ve stateless çalışma"
            ],
            [
                "Dockerfile ve deployment manifestlerini birlikte değerlendirmeli",
                "güvenlik ve operasyon risklerini ayrı açıklamalı",
                "uygun base image veya non-root çalışma önermeli",
                "kanıtı olmayan altyapı konularında varsayım yapmamalı."
            ]),
        new(
            MetricId.M09,
            "m09",
            "Performans, Dayanıklılık ve Ölçeklenebilirlik",
            "UseCase §6.9",
            [
                "Asenkron programlama", "blocking çağrılar", "N+1 sorgular", "cache kullanımı",
                "timeout", "retry", "circuit breaker", "background job", "idempotency",
                "büyük veri sorguları", "stateless tasarım ve yatay ölçeklenebilirlik"
            ],
            [
                "potansiyel ve kesin performans sorunlarını ayırmalı (`confidence`)",
                "sorunun etkisini açıklamalı",
                "ölçüm gerektiren konularda kesin hüküm vermemeli",
                "profiling veya yük testi ihtiyacını belirtebilmeli."
            ]),
        new(
            MetricId.M10,
            "m10",
            "Dokümantasyon ve Mimari Yönetişim",
            "UseCase §6.10",
            [
                "README", "kurulum açıklamaları", "mimari diyagram", "API dokümantasyonu",
                "ADR kayıtları", "teknoloji envanteri", "uygulama sahibi",
                "destek modeli ve operasyonel sorumluluklar"
            ],
            [
                "dokümanın yalnızca varlığını değil yeterliliğini değerlendirmeli",
                "eksikleri göstermeli",
                "koddan çıkarılan mimari görünümü özetlemeli",
                "kullanılabilir içerik şablonu önermeli.",
                "Owner/destek bilgisi repo'da yoksa uydurmamalı."
            ])
    ];

    private static void WithCatalogCopy(Action<string> assertion)
    {
        var scratchParent = Path.Combine(AppContext.BaseDirectory, ".prompt-catalog-tests");
        var scratchRoot = Path.Combine(scratchParent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratchRoot);
        try
        {
            CopyDirectory(PromptRoot, scratchRoot);
            assertion(scratchRoot);
        }
        finally
        {
            Directory.Delete(scratchRoot, recursive: true);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(
                destination,
                Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
        }
    }

    [GeneratedRegex("^[0-9a-f]{12}$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    private sealed record ExpectedMetric(
        MetricId Id,
        string MetricCode,
        string Name,
        string SourceRef,
        string[] ControlAtoms,
        string[] AcceptanceCriteria);
}
