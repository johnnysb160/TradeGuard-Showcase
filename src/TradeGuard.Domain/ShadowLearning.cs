using System.Security.Cryptography;
using System.Text;

namespace TradeGuard.Domain;

/// <summary>
/// Vetor causal e versionado usado pelo aprendizado em sombra. Ele usa somente candles já fechados no instante do
/// sinal; preço futuro, resultado da operação e dados posteriores nunca entram nas características.
/// </summary>
public sealed record ShadowFeatureVector(string SchemaId, IReadOnlyList<double> Values);

public static class ShadowFeatureSchema
{
    public const int Version = 1;

    public static readonly IReadOnlyList<string> Names =
    [
        "instrument_win", "side_buy", "minute_sin", "minute_cos",
        "rule_move_directional_adr", "rule_excess_adr", "day_move_directional_adr",
        "move_30m_directional_adr", "move_60m_directional_adr", "range_so_far_adr",
        "location_in_range", "last_body_directional_adr", "last_range_adr", "trend_alignment",
        .. SetupNames.TimeRules.Select((_, i) => $"setup_{i:00}")
    ];

    public static string Id { get; } = $"signal-v{Version}-" + Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', Names)))).Substring(0, 16);

    public static ShadowFeatureVector Build(
        string instrument, string setup, Side side, DateTimeOffset observedAtUtc,
        decimal adr, IReadOnlyList<Candle> closedCandles)
    {
        var beforeSignal = closedCandles.Where(c => c.OpenUtc < observedAtUtc).OrderBy(c => c.OpenUtc).ToList();
        var day = DateOnly.FromDateTime(B3Clock.ToB3(observedAtUtc).DateTime);
        var today = beforeSignal.Where(c => DateOnly.FromDateTime(B3Clock.ToB3(c.OpenUtc).DateTime) == day).ToList();
        var last = today.LastOrDefault();
        var sign = side.Sign();
        var safeAdr = adr > 0 ? adr : 1m;
        var minute = B3Clock.ToB3(observedAtUtc).TimeOfDay.TotalMinutes;
        var angle = 2d * Math.PI * minute / (24d * 60d);

        decimal ruleMove = 0;
        var rule = SetupNames.TimeRuleOf(setup);
        if (rule is not null && today.Count > 0) ruleMove = rule.MeasureNow(today) ?? 0;

        var dayOpen = today.FirstOrDefault()?.Open ?? last?.Close ?? 0;
        var dayMove = last is null ? 0 : last.Close - dayOpen;
        var high = today.Count == 0 ? 0 : today.Max(c => c.High);
        var low = today.Count == 0 ? 0 : today.Min(c => c.Low);
        var range = high - low;
        var location = last is null || range <= 0 ? 0.5m : (last.Close - low) / range;
        var trend = beforeSignal.Count < 26 ? Trend.Sideways : Indicators.TrendOf(beforeSignal);
        var trendSign = trend == Trend.Up ? 1 : trend == Trend.Down ? -1 : 0;

        decimal DirectionalMove(int bars)
        {
            if (today.Count < bars || last is null) return 0;
            return (last.Close - today[^bars].Open) * sign / safeAdr;
        }

        var values = new List<double>(Names.Count)
        {
            instrument.StartsWith("WIN", StringComparison.OrdinalIgnoreCase) ? 1 : 0,
            side == Side.Buy ? 1 : 0,
            Math.Sin(angle), Math.Cos(angle),
            (double)(ruleMove * sign / safeAdr),
            (double)(Math.Max(0, Math.Abs(ruleMove) / safeAdr - (rule?.MinMoveAdr ?? 0))),
            (double)(dayMove * sign / safeAdr),
            (double)DirectionalMove(6),
            (double)DirectionalMove(12),
            (double)(range / safeAdr),
            (double)location,
            (double)(last is null ? 0 : (last.Close - last.Open) * sign / safeAdr),
            (double)(last is null ? 0 : (last.High - last.Low) / safeAdr),
            trendSign * sign
        };
        for (var i = 0; i < SetupNames.TimeRules.Count; i++)
            values.Add(SetupNames.TimeRules[i].Name == setup ? 1 : 0);

        if (values.Count != Names.Count || values.Any(v => !double.IsFinite(v)))
            throw new InvalidOperationException("Vetor de características inválido.");
        return new ShadowFeatureVector(Id, values);
    }
}

/// <summary>
/// Contrato portátil do modelo. A marca ShadowOnly é obrigatória: este artefato não tem autorização operacional.
/// Means/Scales foram calculados somente no treino e Coefficients formam uma regressão logística simples.
/// </summary>
public sealed record ShadowModelArtifact(
    int ArtifactVersion,
    string ModelId,
    string FeatureSchemaId,
    IReadOnlyList<string> FeatureNames,
    DateTimeOffset CreatedAtUtc,
    DateOnly TrainedThrough,
    bool ShadowOnly,
    string EvidenceStatus,
    double Intercept,
    IReadOnlyList<double> Coefficients,
    IReadOnlyList<double> Means,
    IReadOnlyList<double> Scales,
    double Threshold);

public sealed record ShadowPrediction(string ModelId, double ProbabilityPositive, bool WouldSelect);

/// <summary>Inferência pura. Não conhece ponte, ordens, risco ou motor de trading.</summary>
public sealed class ShadowScorer
{
    private readonly ShadowModelArtifact _artifact;

    private ShadowScorer(ShadowModelArtifact artifact) => _artifact = artifact;

    public string ModelId => _artifact.ModelId;

    public static bool TryCreate(ShadowModelArtifact? artifact, out ShadowScorer? scorer, out string error)
    {
        scorer = null;
        error = "";
        if (artifact is null) return Invalid(out error, "artefato vazio");
        if (artifact.ArtifactVersion != 1) return Invalid(out error, "versão de artefato não suportada");
        if (!artifact.ShadowOnly) return Invalid(out error, "artefato sem a trava ShadowOnly");
        if (string.IsNullOrWhiteSpace(artifact.ModelId)) return Invalid(out error, "ModelId vazio");
        if (artifact.FeatureSchemaId != ShadowFeatureSchema.Id) return Invalid(out error, "schema de características incompatível");
        if (!artifact.FeatureNames.SequenceEqual(ShadowFeatureSchema.Names)) return Invalid(out error, "nomes/ordem das características incompatíveis");
        var count = ShadowFeatureSchema.Names.Count;
        if (artifact.Coefficients.Count != count || artifact.Means.Count != count || artifact.Scales.Count != count)
            return Invalid(out error, "dimensões do modelo incompatíveis");
        if (!double.IsFinite(artifact.Intercept) || artifact.Coefficients.Any(v => !double.IsFinite(v))
            || artifact.Means.Any(v => !double.IsFinite(v)) || artifact.Scales.Any(v => !double.IsFinite(v) || v <= 0))
            return Invalid(out error, "coeficientes ou normalização inválidos");
        if (!double.IsFinite(artifact.Threshold) || artifact.Threshold is <= 0 or >= 1)
            return Invalid(out error, "limiar deve estar entre zero e um");
        scorer = new ShadowScorer(artifact);
        return true;
    }

    public bool TryPredict(ShadowFeatureVector features, DateTimeOffset observedAtUtc, out ShadowPrediction? prediction, out string error)
    {
        prediction = null;
        error = "";
        if (features.SchemaId != ShadowFeatureSchema.Id || features.Values.Count != ShadowFeatureSchema.Names.Count)
            return Invalid(out error, "vetor incompatível");
        if (features.Values.Any(v => !double.IsFinite(v))) return Invalid(out error, "vetor contém valor inválido");
        var observedDay = DateOnly.FromDateTime(B3Clock.ToB3(observedAtUtc).DateTime);
        if (observedDay <= _artifact.TrainedThrough)
            return Invalid(out error, $"observação {observedDay:yyyy-MM-dd} não é posterior ao treino {_artifact.TrainedThrough:yyyy-MM-dd}");

        var z = _artifact.Intercept;
        for (var i = 0; i < features.Values.Count; i++)
            z += ((features.Values[i] - _artifact.Means[i]) / _artifact.Scales[i]) * _artifact.Coefficients[i];
        var probability = z >= 0 ? 1d / (1d + Math.Exp(-z)) : Math.Exp(z) / (1d + Math.Exp(z));
        prediction = new ShadowPrediction(_artifact.ModelId, probability, probability >= _artifact.Threshold);
        return true;
    }

    private static bool Invalid(out string error, string reason) { error = reason; return false; }
}
