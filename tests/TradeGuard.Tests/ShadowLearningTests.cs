using TradeGuard.Domain;

namespace TradeGuard.Tests;

public sealed class ShadowLearningTests
{
    [Fact]
    public void FeatureVector_IsCausalFiniteAndSchemaStable()
    {
        var start = new DateTimeOffset(2026, 9, 29, 9, 0, 0, TimeSpan.FromHours(-3)).ToUniversalTime();
        var bars = Enumerable.Range(0, 30).Select(i => new Candle(start.AddMinutes(5 * i),
            100 + i, 102 + i, 99 + i, 101 + i)).ToList();
        var at = start.AddMinutes(5 * 20);
        var setup = SetupNames.TimeRules[0].Name;

        var withoutFuture = ShadowFeatureSchema.Build("WDO", setup, Side.Buy, at, 100, bars.Take(20).ToList());
        var withFuture = ShadowFeatureSchema.Build("WDO", setup, Side.Buy, at, 100, bars);

        Assert.Equal(ShadowFeatureSchema.Id, withoutFuture.SchemaId);
        Assert.Equal(ShadowFeatureSchema.Names.Count, withoutFuture.Values.Count);
        Assert.Equal(withoutFuture.Values, withFuture.Values);
        Assert.All(withFuture.Values, v => Assert.True(double.IsFinite(v)));
        Assert.Equal(1d, withFuture.Values[14]); // one-hot da primeira regra
        Assert.Equal(1, withFuture.Values.Skip(14).Count(v => v == 1d));
    }

    [Fact]
    public void Scorer_RequiresShadowOnlyAndExactSchema()
    {
        var invalid = Artifact(shadowOnly: false);
        Assert.False(ShadowScorer.TryCreate(invalid, out _, out var error));
        Assert.Contains("ShadowOnly", error);

        var wrongNames = Artifact() with { FeatureNames = ["errado"] };
        Assert.False(ShadowScorer.TryCreate(wrongNames, out _, out error));
        Assert.Contains("características", error);
    }

    [Fact]
    public void Scorer_RejectsLeakageAndScoresOnlyFutureObservation()
    {
        Assert.True(ShadowScorer.TryCreate(Artifact(), out var scorer, out var createError), createError);
        var vector = new ShadowFeatureVector(ShadowFeatureSchema.Id, new double[ShadowFeatureSchema.Names.Count]);
        var trainedThrough = new DateTimeOffset(2026, 9, 28, 15, 0, 0, TimeSpan.FromHours(-3));

        Assert.False(scorer!.TryPredict(vector, trainedThrough, out _, out var leakageError));
        Assert.Contains("não é posterior", leakageError);

        Assert.True(scorer.TryPredict(vector, trainedThrough.AddDays(1), out var prediction, out var scoreError), scoreError);
        Assert.Equal(0.5d, prediction!.ProbabilityPositive, 12);
        Assert.False(prediction.WouldSelect);
    }

    [Fact]
    public void AutomaticTrainer_AcceptsStableAdvantageAndIsDeterministic()
    {
        var examples = SyntheticExamples(predictive: true);

        var first = ShadowAutomaticTrainer.Train(examples, 1);
        var second = ShadowAutomaticTrainer.Train(examples, 1);

        Assert.True(first.Report.Accepted, first.Report.Detail);
        Assert.NotNull(first.Artifact);
        Assert.Equal(first.Artifact!.ModelId, second.Artifact!.ModelId);
        Assert.True(first.Report.Gates.Values.All(x => x));
        Assert.True(first.Report.Holdout!.AverageR > first.Report.Holdout.BaselineAverageR);
    }

    [Fact]
    public void AutomaticTrainer_RejectsDatasetWithoutUsefulSignal()
    {
        var result = ShadowAutomaticTrainer.Train(SyntheticExamples(predictive: false), 1);

        Assert.False(result.Report.Accepted);
        Assert.Null(result.Artifact);
    }

    private static IReadOnlyList<ShadowTrainingExample> SyntheticExamples(bool predictive)
    {
        var start = new DateTimeOffset(2025, 1, 2, 12, 0, 0, TimeSpan.Zero);
        var list = new List<ShadowTrainingExample>();
        for (var day = 0; day < 100; day++)
        for (var trade = 0; trade < 3; trade++)
        {
            var positive = (day + trade) % 2 == 0;
            var features = new double[ShadowFeatureSchema.Names.Count];
            features[0] = predictive ? (positive ? 2 : -2) : 0;
            list.Add(new ShadowTrainingExample(start.AddDays(day).AddMinutes(trade), trade % 2 == 0 ? "WDO" : "WIN",
                SetupNames.TimeRules[trade % SetupNames.TimeRules.Count].Name, features, positive ? 1m : -1m));
        }
        return list;
    }

    private static ShadowModelArtifact Artifact(bool shadowOnly = true)
    {
        var n = ShadowFeatureSchema.Names.Count;
        return new ShadowModelArtifact(1, "teste-v1", ShadowFeatureSchema.Id, ShadowFeatureSchema.Names,
            new DateTimeOffset(2026, 9, 28, 20, 0, 0, TimeSpan.Zero), new DateOnly(2026, 9, 28),
            shadowOnly, "teste", 0, new double[n], new double[n], Enumerable.Repeat(1d, n).ToArray(), 0.6);
    }
}
