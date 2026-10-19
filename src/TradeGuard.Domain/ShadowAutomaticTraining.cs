using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TradeGuard.Domain;

public sealed record ShadowTrainingExample(
    DateTimeOffset AtUtc,
    string Instrument,
    string Setup,
    IReadOnlyList<double> Features,
    decimal RMultiple);

public sealed record ShadowTrainingMetrics(
    int Total,
    int Selected,
    double Coverage,
    decimal BaselineNetR,
    decimal BaselineAverageR,
    decimal BaselineProfitFactor,
    decimal BaselineMaxDrawdownR,
    decimal NetR,
    decimal AverageR,
    decimal ProfitFactor,
    decimal MaxDrawdownR,
    double Brier);

public sealed record ShadowAutomaticTrainingReport(
    int SchemaVersion,
    string DataFingerprint,
    int Generation,
    DateTimeOffset GeneratedAtUtc,
    int Samples,
    int Sessions,
    DateOnly? TrainThrough,
    DateOnly? SelectionThrough,
    DateOnly? EvidenceThrough,
    double? L2,
    double? Threshold,
    ShadowTrainingMetrics? Selection,
    ShadowTrainingMetrics? Holdout,
    IReadOnlyDictionary<string, bool> Gates,
    bool Accepted,
    string Detail);

public sealed record ShadowAutomaticTrainingResult(
    ShadowModelArtifact? Artifact,
    ShadowAutomaticTrainingReport Report);

/// <summary>
/// Treinador determinístico e sem acesso à execução. Seleciona hiperparâmetros numa janela intermediária e abre o
/// holdout exatamente uma vez. Se não houver vantagem líquida, devolve somente o relatório de reprovação.
/// </summary>
public static class ShadowAutomaticTrainer
{
    private static readonly double[] L2Grid = [0.005, 0.02, 0.08, 0.20];
    private static readonly double[] ThresholdGrid = Enumerable.Range(30, 46).Select(x => x / 100d).ToArray();

    public static ShadowAutomaticTrainingResult Train(IReadOnlyList<ShadowTrainingExample> raw, int generation)
    {
        var examples = raw
            .Where(Valid)
            .OrderBy(x => x.AtUtc)
            .ToList();
        var fingerprint = Fingerprint(examples);
        var days = examples.Select(Day).Distinct().Order().ToList();

        if (examples.Count < 200 || days.Count < 60)
            return Reject(fingerprint, generation, examples.Count, days.Count,
                $"Aguardando amostra: {examples.Count}/200 resultados e {days.Count}/60 pregões.");

        var trainEnd = days[(int)(days.Count * 0.60) - 1];
        var selectionEnd = days[(int)(days.Count * 0.80) - 1];
        var train = examples.Where(x => Day(x) <= trainEnd).ToList();
        var selection = examples.Where(x => Day(x) > trainEnd && Day(x) <= selectionEnd).ToList();
        var holdout = examples.Where(x => Day(x) > selectionEnd).ToList();
        if (selection.Count < 30 || holdout.Count < 30)
            return Reject(fingerprint, generation, examples.Count, days.Count,
                "As janelas temporais ficaram pequenas demais para uma comparação segura.", trainEnd, selectionEnd, days[^1]);

        Candidate? chosen = null;
        foreach (var l2 in L2Grid)
        {
            var fitted = Fit(train, l2);
            var candidate = ThresholdGrid
                .Select(threshold => new Candidate(l2, threshold, fitted, Metrics(selection, fitted, threshold)))
                .Where(x => x.Selection.Selected >= 20 && x.Selection.Coverage is >= 0.30 and <= 0.85)
                .OrderByDescending(x => Utility(x.Selection))
                .ThenBy(x => x.Selection.MaxDrawdownR)
                .FirstOrDefault();
            if (candidate is not null && (chosen is null || Utility(candidate.Selection) > Utility(chosen.Selection)))
                chosen = candidate;
        }

        if (chosen is null)
            return Reject(fingerprint, generation, examples.Count, days.Count,
                "Nenhuma configuração manteve cobertura e amostra mínimas.", trainEnd, selectionEnd, days[^1]);

        // Depois da escolha, reajusta nos 80% antigos. O holdout final continua intocado.
        var refit = Fit([.. train, .. selection], chosen.L2);
        var holdoutMetrics = Metrics(holdout, refit, chosen.Threshold);
        var selectionGates = PerformanceGates(chosen.Selection);
        var holdoutGates = PerformanceGates(holdoutMetrics);
        var gates = new Dictionary<string, bool>
        {
            ["selecao_cobertura"] = selectionGates.Coverage,
            ["selecao_expectativa"] = selectionGates.Average,
            ["selecao_pf"] = selectionGates.ProfitFactor,
            ["selecao_drawdown"] = selectionGates.Drawdown,
            ["holdout_cobertura"] = holdoutGates.Coverage,
            ["holdout_expectativa"] = holdoutGates.Average,
            ["holdout_pf"] = holdoutGates.ProfitFactor,
            ["holdout_drawdown"] = holdoutGates.Drawdown,
            ["holdout_calibracao"] = BetterThanClimatology(holdout, holdoutMetrics.Brier)
        };
        var accepted = gates.Values.All(x => x);
        var evidenceThrough = days[^1];
        ShadowModelArtifact? artifact = null;
        if (accepted)
        {
            var identity = $"{fingerprint}|{generation}|{chosen.L2:R}|{chosen.Threshold:R}|{ShadowFeatureSchema.Id}";
            var modelId = "auto-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
            artifact = new ShadowModelArtifact(1, modelId, ShadowFeatureSchema.Id, ShadowFeatureSchema.Names,
                DateTimeOffset.UtcNow, evidenceThrough, true,
                $"geracao automatica {generation}; walk-forward 60/20/20 aprovado; autoridade inicial somente sombra",
                refit.Intercept, refit.Coefficients, refit.Means, refit.Scales, chosen.Threshold);
        }

        var detail = accepted
            ? "Candidato venceu seleção e holdout; instalado automaticamente em sombra."
            : "Candidato reprovado automaticamente: " + string.Join(", ", gates.Where(x => !x.Value).Select(x => x.Key));
        return new ShadowAutomaticTrainingResult(artifact,
            new ShadowAutomaticTrainingReport(1, fingerprint, generation, DateTimeOffset.UtcNow, examples.Count,
                days.Count, trainEnd, selectionEnd, evidenceThrough, chosen.L2, chosen.Threshold,
                chosen.Selection, holdoutMetrics, gates, accepted, detail));
    }

    private static bool Valid(ShadowTrainingExample x) =>
        x.Features.Count == ShadowFeatureSchema.Names.Count &&
        x.Features.All(double.IsFinite) &&
        !string.IsNullOrWhiteSpace(x.Instrument);

    private static ShadowAutomaticTrainingResult Reject(string fingerprint, int generation, int samples, int sessions,
        string detail, DateOnly? trainThrough = null, DateOnly? selectionThrough = null, DateOnly? evidenceThrough = null) =>
        new(null, new ShadowAutomaticTrainingReport(1, fingerprint, generation, DateTimeOffset.UtcNow, samples,
            sessions, trainThrough, selectionThrough, evidenceThrough, null, null, null, null,
            new Dictionary<string, bool>(), false, detail));

    private static DateOnly Day(ShadowTrainingExample x) => DateOnly.FromDateTime(B3Clock.ToB3(x.AtUtc).DateTime);

    private static string Fingerprint(IEnumerable<ShadowTrainingExample> examples)
    {
        var text = new StringBuilder(4096);
        foreach (var x in examples)
        {
            text.Append(x.AtUtc.UtcTicks).Append('|').Append(x.Instrument).Append('|').Append(x.Setup).Append('|')
                .Append(x.RMultiple.ToString(CultureInfo.InvariantCulture));
            foreach (var value in x.Features) text.Append('|').Append(value.ToString("R", CultureInfo.InvariantCulture));
            text.AppendLine();
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static decimal Utility(ShadowTrainingMetrics x) =>
        x.AverageR * 100m + x.NetR - x.MaxDrawdownR * 0.50m;

    private static (bool Coverage, bool Average, bool ProfitFactor, bool Drawdown) PerformanceGates(ShadowTrainingMetrics x) =>
        (x.Coverage is >= 0.30 and <= 0.85,
            x.AverageR >= x.BaselineAverageR + 0.05m,
            x.ProfitFactor > x.BaselineProfitFactor,
            x.MaxDrawdownR <= x.BaselineMaxDrawdownR);

    private static bool BetterThanClimatology(IReadOnlyList<ShadowTrainingExample> examples, double brier)
    {
        var prevalence = examples.Count == 0 ? 0 : examples.Count(x => x.RMultiple > 0) / (double)examples.Count;
        return examples.Count > 0 && brier < prevalence * (1 - prevalence);
    }

    private static Fitted Fit(IReadOnlyList<ShadowTrainingExample> train, double l2)
    {
        var n = ShadowFeatureSchema.Names.Count;
        var means = new double[n];
        var scales = new double[n];
        for (var j = 0; j < n; j++)
        {
            means[j] = train.Average(x => x.Features[j]);
            scales[j] = Math.Sqrt(train.Average(x => Math.Pow(x.Features[j] - means[j], 2)));
            if (scales[j] < 1e-9) scales[j] = 1;
        }
        var positiveRate = Math.Clamp(train.Count(x => x.RMultiple > 0) / (double)train.Count, 0.01, 0.99);
        var intercept = Math.Log(positiveRate / (1 - positiveRate));
        var coefficients = new double[n];
        const double learningRate = 0.08;
        for (var iteration = 0; iteration < 3000; iteration++)
        {
            var gradient = new double[n];
            var interceptGradient = 0d;
            foreach (var x in train)
            {
                var z = intercept;
                for (var j = 0; j < n; j++) z += coefficients[j] * ((x.Features[j] - means[j]) / scales[j]);
                var p = Sigmoid(z);
                var error = p - (x.RMultiple > 0 ? 1 : 0);
                interceptGradient += error;
                for (var j = 0; j < n; j++) gradient[j] += error * ((x.Features[j] - means[j]) / scales[j]);
            }
            intercept -= learningRate * interceptGradient / train.Count;
            for (var j = 0; j < n; j++)
                coefficients[j] -= learningRate * (gradient[j] / train.Count + l2 * coefficients[j]);
        }
        return new Fitted(intercept, coefficients, means, scales);
    }

    private static ShadowTrainingMetrics Metrics(IReadOnlyList<ShadowTrainingExample> examples, Fitted fitted, double threshold)
    {
        var scored = examples.Select(x => (Example: x, Probability: Predict(x, fitted))).ToList();
        var selected = scored.Where(x => x.Probability >= threshold).Select(x => x.Example).ToList();
        var baseline = Curve(examples.Select(x => x.RMultiple));
        var filtered = Curve(selected.Select(x => x.RMultiple));
        var brier = scored.Count == 0 ? 0 : scored.Average(x => Math.Pow(x.Probability - (x.Example.RMultiple > 0 ? 1 : 0), 2));
        return new ShadowTrainingMetrics(examples.Count, selected.Count,
            examples.Count == 0 ? 0 : selected.Count / (double)examples.Count,
            baseline.Net, baseline.Average, baseline.ProfitFactor, baseline.Drawdown,
            filtered.Net, filtered.Average, filtered.ProfitFactor, filtered.Drawdown, Math.Round(brier, 6));
    }

    private static (decimal Net, decimal Average, decimal ProfitFactor, decimal Drawdown) Curve(IEnumerable<decimal> values)
    {
        var list = values.ToList();
        decimal net = 0, peak = 0, drawdown = 0;
        foreach (var value in list)
        {
            net += value;
            peak = Math.Max(peak, net);
            drawdown = Math.Max(drawdown, peak - net);
        }
        var gain = list.Where(x => x > 0).Sum();
        var loss = -list.Where(x => x < 0).Sum();
        return (Math.Round(net, 3), list.Count == 0 ? 0 : Math.Round(net / list.Count, 4),
            loss == 0 ? 99 : Math.Round(gain / loss, 3), Math.Round(drawdown, 3));
    }

    private static double Predict(ShadowTrainingExample x, Fitted fitted)
    {
        var z = fitted.Intercept;
        for (var i = 0; i < x.Features.Count; i++)
            z += fitted.Coefficients[i] * ((x.Features[i] - fitted.Means[i]) / fitted.Scales[i]);
        return Sigmoid(z);
    }

    private static double Sigmoid(double z) => z >= 0 ? 1 / (1 + Math.Exp(-z)) : Math.Exp(z) / (1 + Math.Exp(z));
    private sealed record Fitted(double Intercept, double[] Coefficients, double[] Means, double[] Scales);
    private sealed record Candidate(double L2, double Threshold, Fitted Fitted, ShadowTrainingMetrics Selection);
}
