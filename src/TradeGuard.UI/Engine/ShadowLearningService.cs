using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradeGuard.Domain;

namespace TradeGuard.UI.Engine;

public sealed record ShadowLearningStatus(string State, string Headline, string Detail, int Samples = 0, int Required = 200)
{
    public bool ReviewReady => State == "review_ready";
}

/// <summary>Executa o ciclo periodicamente e recupera sozinho qualquer execução perdida após reinício.</summary>
public sealed class ShadowLearningCycleHost(ShadowLearningService learning, ILogger<ShadowLearningCycleHost> logger)
    : Microsoft.Extensions.Hosting.BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await learning.RunAutomaticCycleAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError(ex, "Falha no ciclo automático de inteligência; as 11 regras continuam."); }
            await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken);
        }
    }
}

public sealed record ShadowReviewNotice(string Title, string Detail);

public sealed record ShadowDemoDecision(bool Active, bool Allow, string? ModelId, double? Probability)
{
    public static readonly ShadowDemoDecision Inactive = new(false, true, null, null);
}

public sealed record ShadowCycleState(
    int SchemaVersion,
    int Generation,
    string State,
    string? ModelId,
    string? LastDataFingerprint,
    int SamplesAtLastAttempt,
    int SessionsAtLastAttempt,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? DemoStartedAtUtc,
    string Detail);

/// <summary>
/// Observador append-only e orquestrador do ciclo. Não conhece BridgeServer/OpenCommand; só expõe ao motor um veto
/// limitado ao piloto demo depois dos portões. Falha, ausência ou incompatibilidade sempre devolve o controle às regras.
/// </summary>
public sealed class ShadowLearningService
{
    private const int EventSchemaVersion = 1;
    private readonly object _sync = new();
    private readonly ILogger<ShadowLearningService> _logger;
    private readonly string _directory;
    private ShadowScorer? _scorer;
    private ShadowModelArtifact? _artifact;
    private ShadowScorer? _demoScorer;
    private ShadowModelArtifact? _demoArtifact;
    private string _modelStatus = "coleta-sem-modelo";
    private ShadowCycleState _cycle;
    private readonly Dictionary<string, ShadowPrediction> _predictions = new(StringComparer.Ordinal);
    private DateTimeOffset _lastWriteWarning;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public ShadowLearningStatus Status { get; private set; } =
        new("starting", "Aprendizado em sombra iniciando", "O processo é automático e não interfere nas ordens.");

    public ShadowLearningService(ILogger<ShadowLearningService> logger)
    {
        _logger = logger;
        _directory = Path.Combine(TradeGuardServices.DataDirectory, "ia-sombra");
        Directory.CreateDirectory(_directory);
        _cycle = LoadCycleState();
        LoadOperationalModel();
        LoadCandidate();
        if (_artifact is not null) UpdateProspectiveSummary();
        else SetWaitingStatus();
    }

    public string ObserveSignal(
        string instrument, EntrySignal signal, DateTimeOffset atUtc, decimal adr,
        decimal bid, decimal ask, IReadOnlyList<Candle> closedCandles, FlowReading flow)
    {
        var signalId = Guid.NewGuid().ToString("N");
        try
        {
            var features = ShadowFeatureSchema.Build(instrument, signal.Setup, signal.Side, atUtc, adr, closedCandles);
            ShadowPrediction? prediction = null;
            var scoreStatus = _modelStatus;
            ShadowScorer? candidateScorer, demoScorer;
            string? demoModelId;
            lock (_sync)
            {
                candidateScorer = _scorer;
                demoScorer = _demoScorer;
                demoModelId = _demoArtifact?.ModelId;
            }
            if (candidateScorer is not null && !candidateScorer.TryPredict(features, atUtc, out prediction, out var error))
                scoreStatus = "score-recusado: " + error;

            ShadowPrediction? operationalPrediction = prediction;
            if (demoScorer is not null && demoModelId != prediction?.ModelId
                && demoScorer.TryPredict(features, atUtc, out var demoPrediction, out _)
                && demoPrediction is not null)
                operationalPrediction = demoPrediction;
            if (operationalPrediction is not null)
            {
                lock (_sync)
                {
                    _predictions[signalId] = operationalPrediction;
                    if (_predictions.Count > 2_000)
                        foreach (var old in _predictions.Keys.Take(_predictions.Count - 1_500).ToList())
                            _predictions.Remove(old);
                }
            }

            Write(new ShadowEventPayload(
                EventSchemaVersion, "signal", Guid.NewGuid().ToString("N"), signalId, atUtc,
                instrument, signal.Setup, signal.Side.ToString().ToLowerInvariant(), null, signal.Reason,
                features.SchemaId, features.Values, scoreStatus, prediction?.ModelId,
                prediction?.ProbabilityPositive, prediction?.WouldSelect, bid, ask, adr,
                new ShadowFlowContext(flow.HasData, flow.Delta5s, flow.Delta30s, flow.Delta1m, flow.DeltaDay,
                    flow.BookImbalance, flow.Bias.ToString(), flow.Strong, flow.Absorption.ToString()),
                null, null, null));
        }
        catch (Exception ex)
        {
            WarnWrite(ex, "Falha ao montar a observação em sombra");
        }
        return signalId;
    }

    /// <summary>
    /// Só tem autoridade durante o piloto automático em conta demo. Ausência, erro ou estado diferente sempre
    /// preserva a decisão das 11 regras.
    /// </summary>
    public ShadowDemoDecision DemoDecision(string signalId)
    {
        lock (_sync)
        {
            if (_demoArtifact is null)
                return ShadowDemoDecision.Inactive;
            return _predictions.TryGetValue(signalId, out var prediction) && prediction.ModelId == _demoArtifact.ModelId
                ? new ShadowDemoDecision(true, prediction.WouldSelect, prediction.ModelId, prediction.ProbabilityPositive)
                : ShadowDemoDecision.Inactive;
        }
    }

    public void RecordDisposition(string signalId, DateTimeOffset atUtc, string instrument, string state, string? detail = null, long? positionId = null) =>
        SafeWrite(new ShadowEventPayload(EventSchemaVersion, "disposition", Guid.NewGuid().ToString("N"), signalId, atUtc,
            instrument, null, null, state, detail, null, null, _modelStatus, null, null, null,
            null, null, null, null, null, null, positionId));

    public ShadowReviewNotice? RecordOutcome(string signalId, DateTimeOffset atUtc, string instrument, string state,
        decimal pnlMoney, decimal rMultiple, long? positionId, string? detail = null)
    {
        SafeWrite(new ShadowEventPayload(EventSchemaVersion, "outcome", Guid.NewGuid().ToString("N"), signalId, atUtc,
            instrument, null, null, state, detail, null, null, _modelStatus, null, null, null,
            null, null, null, null, pnlMoney, rMultiple, positionId));
        UpdateProspectiveSummary();
        return TakeReviewNotice();
    }

    private void SafeWrite(ShadowEventPayload payload)
    {
        try { Write(payload); }
        catch (Exception ex) { WarnWrite(ex, "Falha ao gravar evento do aprendizado em sombra"); }
    }

    private void Write(ShadowEventPayload payload)
    {
        var payloadJson = JsonSerializer.Serialize(payload, Json);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson)));
        var envelope = JsonSerializer.Serialize(new ShadowEventEnvelope(hash, JsonSerializer.Deserialize<JsonElement>(payloadJson)), Json);
        lock (_sync)
        {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, $"eventos-{payload.AtUtc:yyyy-MM}.jsonl");
            File.AppendAllText(path, envelope + Environment.NewLine, new UTF8Encoding(false));
        }
    }

    private void WarnWrite(Exception ex, string message)
    {
        if (DateTimeOffset.UtcNow - _lastWriteWarning < TimeSpan.FromMinutes(5)) return;
        _lastWriteWarning = DateTimeOffset.UtcNow;
        _logger.LogWarning(ex, "{Message}. Isso não altera a operação do robô.", message);
    }

    public Task RunAutomaticCycleAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var evidence = ReadTrainingEvidence();
            if (evidence.IntegrityErrors > 0)
            {
                Status = new("cycle_blocked", "Inteligência aguardando dados íntegros",
                    $"{evidence.IntegrityErrors} evento(s) inválido(s); as 11 regras seguem sem alteração.", evidence.Examples.Count);
                return Task.CompletedTask;
            }

            if (_artifact is not null)
            {
                if (FailsStoredRetrospectiveReport(_artifact.ModelId))
                {
                    ArchiveCurrentCandidate("reprovado-retrospectivo");
                    _cycle = _cycle with
                    {
                        State = "candidate_rejected", ModelId = null, UpdatedAtUtc = DateTimeOffset.UtcNow,
                        Detail = "Candidato histórico sem vantagem arquivado; aguardando evidência nova para o sucessor."
                    };
                    SaveCycleState();
                }
                else
                {
                UpdateProspectiveSummary();
                if (Status.State == "evidence_rejected")
                {
                    ArchiveCurrentCandidate("reprovado-prospectivo");
                    _cycle = _cycle with
                    {
                        State = "candidate_rejected", ModelId = null, UpdatedAtUtc = DateTimeOffset.UtcNow,
                        Detail = "Candidato reprovado e arquivado automaticamente; preparando o sucessor."
                    };
                    SaveCycleState();
                }
                else if (Status.State == "review_ready" && _cycle.State == "shadow_active")
                {
                    ActivateDemoPilot();
                    _cycle = _cycle with
                    {
                        State = "demo_canary", DemoStartedAtUtc = DateTimeOffset.UtcNow,
                        UpdatedAtUtc = DateTimeOffset.UtcNow,
                        Detail = "Portões prospectivos aprovados; filtro ativado automaticamente apenas no simulador."
                    };
                    SaveCycleState();
                    Status = new("demo_canary", "Filtro em piloto automático no simulador",
                        "O candidato agora bloqueia somente os sinais que reprovou; risco, stop e saídas continuam nas 11 regras.",
                        evidence.Examples.Count);
                    return Task.CompletedTask;
                }
                else if (_cycle.State is "demo_canary" or "demo_champion")
                {
                    EvaluateDemoCanary(evidence);
                    if (_cycle.State != "demo_champion") return Task.CompletedTask;
                    var championSessions = evidence.Examples.Select(x => DateOnly.FromDateTime(B3Clock.ToB3(x.AtUtc).DateTime)).Distinct().Count();
                    var canChallenge = evidence.Examples.Count >= _cycle.SamplesAtLastAttempt + 50
                                       || championSessions >= _cycle.SessionsAtLastAttempt + 20;
                    if (!canChallenge) return Task.CompletedTask;
                    ReleaseCandidateSlotForSuccessor();
                }
                else
                    return Task.CompletedTask;
                }
            }

            var sessions = evidence.Examples.Select(x => DateOnly.FromDateTime(B3Clock.ToB3(x.AtUtc).DateTime)).Distinct().Count();
            var enoughNewData = _cycle.LastDataFingerprint is null
                || evidence.Examples.Count >= _cycle.SamplesAtLastAttempt + 50
                || sessions >= _cycle.SessionsAtLastAttempt + 20;
            if (evidence.Examples.Count < 200 || sessions < 60 || !enoughNewData)
            {
                SetWaitingStatus(evidence.Examples.Count, sessions);
                return Task.CompletedTask;
            }

            _cycle = _cycle with
            {
                State = "training", UpdatedAtUtc = DateTimeOffset.UtcNow,
                Detail = "Treinando e validando automaticamente um novo candidato."
            };
            SaveCycleState();

            var result = ShadowAutomaticTrainer.Train(evidence.Examples, _cycle.Generation + 1);
            WriteAtomic(Path.Combine(_directory, $"treino-automatico-g{_cycle.Generation + 1:000}.json"),
                JsonSerializer.Serialize(result.Report, new JsonSerializerOptions(Json) { WriteIndented = true }));
            _cycle = _cycle with
            {
                LastDataFingerprint = result.Report.DataFingerprint,
                SamplesAtLastAttempt = evidence.Examples.Count,
                SessionsAtLastAttempt = sessions,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };

            if (result.Artifact is null)
            {
                _cycle = _cycle with { State = "no_candidate", Detail = result.Report.Detail };
                SaveCycleState();
                Status = new("waiting_new_data", "Nenhum candidato melhor por enquanto",
                    "A tentativa foi reprovada automaticamente. O próximo ciclo começa quando houver dados novos.", evidence.Examples.Count);
                _logger.LogInformation("Geração automática {Generation} não produziu candidato: {Detail}",
                    _cycle.Generation + 1, result.Report.Detail);
                return Task.CompletedTask;
            }

            WriteAtomic(Path.Combine(_directory, "modelo-candidato.json"),
                JsonSerializer.Serialize(result.Artifact, new JsonSerializerOptions(Json) { WriteIndented = true }));
            _cycle = _cycle with
            {
                Generation = _cycle.Generation + 1, State = "shadow_active", ModelId = result.Artifact.ModelId,
                DemoStartedAtUtc = null, Detail = "Novo candidato instalado automaticamente em sombra."
            };
            SaveCycleState();
            LoadCandidate();
            UpdateProspectiveSummary();
            _logger.LogInformation("Candidato automático {ModelId} instalado em sombra (geração {Generation}).",
                result.Artifact.ModelId, _cycle.Generation);
        }
        return Task.CompletedTask;
    }

    private void EvaluateDemoCanary(TrainingEvidence evidence)
    {
        if (_artifact is null || _cycle.DemoStartedAtUtc is not { } started) return;
        var live = evidence.ModelOutcomes
            .Where(x => x.ModelId == _artifact.ModelId && x.Selected && x.AtUtc >= started)
            .OrderBy(x => x.AtUtc).Select(x => x.R).ToList();
        var curve = Curve(live);
        if (live.Count >= 15 && (curve.AverageR < -0.05m || curve.MaxDrawdownR > 8m))
        {
            ClearOperationalModel();
            ArchiveCurrentCandidate("rollback-demo");
            _cycle = _cycle with
            {
                State = "rolled_back", ModelId = null, DemoStartedAtUtc = null, UpdatedAtUtc = DateTimeOffset.UtcNow,
                Detail = $"Rollback automático após {live.Count} operações: média {curve.AverageR:0.000}R, DD {curve.MaxDrawdownR:0.0}R."
            };
            SaveCycleState();
            Status = new("rolled_back", "Filtro retirado automaticamente",
                "O piloto degradou e as 11 regras retomaram todo o controle. Um sucessor será treinado com dados novos.", live.Count);
            return;
        }
        if (live.Count >= 50 && curve.AverageR > 0 && curve.ProfitFactor > 1)
        {
            if (_artifact is not null)
            {
                WriteAtomic(Path.Combine(_directory, "modelo-campeao-demo.json"),
                    JsonSerializer.Serialize(_artifact, new JsonSerializerOptions(Json) { WriteIndented = true }));
                var pilot = Path.Combine(_directory, "modelo-piloto-demo.json");
                if (File.Exists(pilot)) File.Delete(pilot);
                _demoArtifact = _artifact;
                _demoScorer = _scorer;
            }
            _cycle = _cycle with
            {
                State = "demo_champion", UpdatedAtUtc = DateTimeOffset.UtcNow,
                Detail = $"Piloto aprovado: {live.Count} operações, média {curve.AverageR:0.000}R, PF {curve.ProfitFactor:0.00}."
            };
            SaveCycleState();
        }
        Status = new(_cycle.State, _cycle.State == "demo_champion" ? "Filtro ativo no simulador" : "Filtro em piloto automático",
            $"{live.Count}/50 operações do piloto; rollback automático se média < -0,05R ou drawdown > 8R.", live.Count, 50);
    }

    private TrainingEvidence ReadTrainingEvidence()
    {
        var signals = new Dictionary<string, SignalEvidence>(StringComparer.Ordinal);
        var outcomes = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var integrityErrors = 0;
        foreach (var file in Directory.EnumerateFiles(_directory, "eventos-*.jsonl").Order())
        foreach (var line in File.ReadLines(file).Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var payload = root.GetProperty("payload");
                var expected = root.GetProperty("payloadSha256").GetString();
                var actual = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload.GetRawText())));
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) { integrityErrors++; continue; }
                var signalId = payload.GetProperty("signalId").GetString();
                if (string.IsNullOrWhiteSpace(signalId)) continue;
                var eventType = payload.GetProperty("eventType").GetString();
                if (eventType == "signal" && payload.TryGetProperty("features", out var vector)
                    && vector.ValueKind == JsonValueKind.Array)
                {
                    var features = vector.EnumerateArray().Select(x => x.GetDouble()).ToArray();
                    string? modelId = null;
                    double? probability = null;
                    bool? selected = null;
                    if (payload.TryGetProperty("modelId", out var model) && model.ValueKind == JsonValueKind.String) modelId = model.GetString();
                    if (payload.TryGetProperty("probabilityPositive", out var p) && p.ValueKind == JsonValueKind.Number) probability = p.GetDouble();
                    if (payload.TryGetProperty("wouldSelect", out var s) && s.ValueKind is JsonValueKind.True or JsonValueKind.False) selected = s.GetBoolean();
                    signals[signalId] = new SignalEvidence(payload.GetProperty("atUtc").GetDateTimeOffset(),
                        payload.GetProperty("instrument").GetString() ?? "", payload.GetProperty("setup").GetString() ?? "",
                        features, modelId, probability, selected);
                }
                else if (eventType == "outcome" && payload.TryGetProperty("rMultiple", out var r) && r.ValueKind == JsonValueKind.Number)
                    outcomes[signalId] = r.GetDecimal();
            }
            catch { integrityErrors++; }
        }
        var matched = signals.Where(x => outcomes.ContainsKey(x.Key)).ToList();
        var examples = matched.Select(x => new ShadowTrainingExample(x.Value.AtUtc, x.Value.Instrument,
            x.Value.Setup, x.Value.Features, outcomes[x.Key])).ToList();
        var modelOutcomes = matched.Where(x => x.Value.ModelId is not null && x.Value.Selected is not null)
            .Select(x => new ModelOutcome(x.Value.AtUtc, x.Value.ModelId!, x.Value.Selected!.Value, outcomes[x.Key])).ToList();
        return new TrainingEvidence(examples, modelOutcomes, integrityErrors);
    }

    private bool FailsStoredRetrospectiveReport(string modelId)
    {
        var path = Path.Combine(_directory, $"treino-{modelId}.json");
        if (!File.Exists(path)) return false;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (!root.TryGetProperty("selection", out var selection)
                || !root.TryGetProperty("historicalHoldout", out var holdout)) return false;
            static bool Better(JsonElement metrics) =>
                metrics.GetProperty("coverage").GetDouble() is >= 0.30 and <= 0.85
                && metrics.GetProperty("averageR").GetDecimal() >= metrics.GetProperty("baselineAverageR").GetDecimal() + 0.05m
                && metrics.GetProperty("profitFactor").GetDecimal() > metrics.GetProperty("baselineProfitFactor").GetDecimal()
                && metrics.GetProperty("maxDrawdownR").GetDecimal() <= metrics.GetProperty("baselineMaxDrawdownR").GetDecimal();
            return !Better(selection) || !Better(holdout);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Relatório retrospectivo {ModelId} não pôde ser validado; mantendo apenas sombra.", modelId);
            return false;
        }
    }

    private void ActivateDemoPilot()
    {
        if (_artifact is null || _scorer is null) return;
        if (_demoArtifact is not null && _demoArtifact.ModelId != _artifact.ModelId)
        {
            var archive = Path.Combine(_directory, "arquivo", $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{_demoArtifact.ModelId}-campeao-substituido");
            Directory.CreateDirectory(archive);
            var old = Path.Combine(_directory, "modelo-campeao-demo.json");
            if (File.Exists(old)) File.Copy(old, Path.Combine(archive, Path.GetFileName(old)), true);
        }
        WriteAtomic(Path.Combine(_directory, "modelo-piloto-demo.json"),
            JsonSerializer.Serialize(_artifact, new JsonSerializerOptions(Json) { WriteIndented = true }));
        _demoArtifact = _artifact;
        _demoScorer = _scorer;
        var champion = Path.Combine(_directory, "modelo-campeao-demo.json");
        if (File.Exists(champion)) File.Delete(champion);
    }

    private void LoadOperationalModel()
    {
        foreach (var name in new[] { "modelo-piloto-demo.json", "modelo-campeao-demo.json" })
        {
            var path = Path.Combine(_directory, name);
            if (!File.Exists(path)) continue;
            try
            {
                var artifact = JsonSerializer.Deserialize<ShadowModelArtifact>(File.ReadAllText(path), Json);
                if (ShadowScorer.TryCreate(artifact, out var scorer, out _))
                {
                    _demoArtifact = artifact;
                    _demoScorer = scorer;
                    return;
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Modelo operacional demo inválido: {Path}", path); }
        }
    }

    private void ClearOperationalModel()
    {
        foreach (var name in new[] { "modelo-piloto-demo.json", "modelo-campeao-demo.json" })
        {
            var path = Path.Combine(_directory, name);
            if (File.Exists(path)) File.Delete(path);
        }
        _demoArtifact = null;
        _demoScorer = null;
    }

    private void ReleaseCandidateSlotForSuccessor()
    {
        var candidate = Path.Combine(_directory, "modelo-candidato.json");
        if (File.Exists(candidate)) File.Delete(candidate);
        _artifact = null;
        _scorer = null;
        _modelStatus = "coleta-com-campeao-demo";
        _cycle = _cycle with
        {
            State = "waiting_successor", ModelId = _demoArtifact?.ModelId, DemoStartedAtUtc = null,
            UpdatedAtUtc = DateTimeOffset.UtcNow, Detail = "Campeão demo mantido; treinando um sucessor em paralelo."
        };
        SaveCycleState();
    }

    private void LoadCandidate()
    {
        _artifact = null;
        _scorer = null;
        var path = Path.Combine(_directory, "modelo-candidato.json");
        if (!File.Exists(path)) { _modelStatus = "coleta-sem-modelo"; return; }
        try
        {
            var artifact = JsonSerializer.Deserialize<ShadowModelArtifact>(File.ReadAllText(path), Json);
            if (!ShadowScorer.TryCreate(artifact, out var scorer, out var error))
            {
                _modelStatus = "modelo-recusado: " + error;
                Status = new("model_rejected", "Candidato recusado com segurança", error);
                return;
            }
            _artifact = artifact;
            _scorer = scorer;
            _modelStatus = "modelo-sombra-valido";
            if (_cycle.ModelId != artifact!.ModelId || _cycle.State is "waiting_data" or "no_candidate" or "candidate_rejected")
            {
                _cycle = _cycle with
                {
                    State = "shadow_active", ModelId = artifact.ModelId, UpdatedAtUtc = DateTimeOffset.UtcNow,
                    Detail = "Candidato congelado em avaliação prospectiva; a próxima decisão é automática."
                };
                SaveCycleState();
            }
            _logger.LogInformation("Modelo {ModelId} carregado no ciclo automático ({State}).", artifact.ModelId, _cycle.State);
        }
        catch (Exception ex)
        {
            _modelStatus = "modelo-recusado: JSON inválido";
            Status = new("model_rejected", "Candidato recusado com segurança", "Artefato inválido; as 11 regras continuam.");
            _logger.LogWarning(ex, "Modelo em sombra ilegível.");
        }
    }

    private ShadowCycleState LoadCycleState()
    {
        var path = Path.Combine(_directory, "ciclo-automatico.json");
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<ShadowCycleState>(File.ReadAllText(path), Json) is { } saved)
                return saved;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Estado do ciclo automático inválido; reconstruindo."); }
        return new ShadowCycleState(1, 0, "waiting_data", null, null, 0, 0, DateTimeOffset.UtcNow, null,
            "Aguardando dados suficientes para o próximo candidato.");
    }

    private void SaveCycleState() => WriteAtomic(Path.Combine(_directory, "ciclo-automatico.json"),
        JsonSerializer.Serialize(_cycle, new JsonSerializerOptions(Json) { WriteIndented = true }));

    private void ArchiveCurrentCandidate(string reason)
    {
        if (_artifact is null) return;
        var archive = Path.Combine(_directory, "arquivo", $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{_artifact.ModelId}-{reason}");
        Directory.CreateDirectory(archive);
        foreach (var path in new[]
                 {
                     Path.Combine(_directory, "modelo-candidato.json"),
                     Path.Combine(_directory, "resumo-prospectivo.json"),
                     Path.Combine(_directory, $"treino-{_artifact.ModelId}.json")
                 }.Where(File.Exists))
            File.Copy(path, Path.Combine(archive, Path.GetFileName(path)), true);
        var modelPath = Path.Combine(_directory, "modelo-candidato.json");
        if (File.Exists(modelPath)) File.Delete(modelPath);
        _artifact = null;
        _scorer = null;
        _modelStatus = "coleta-sem-modelo";
        _predictions.Clear();
        _logger.LogInformation("Candidato arquivado automaticamente em {Archive}: {Reason}.",
            Path.GetFileName(archive), reason);
    }

    private void SetWaitingStatus(int samples = 0, int sessions = 0)
    {
        if (_demoArtifact is not null)
        {
            _cycle = _cycle with
            {
                State = "waiting_successor", ModelId = _demoArtifact.ModelId, UpdatedAtUtc = DateTimeOffset.UtcNow,
                Detail = $"Campeão demo ativo; aguardando dados para o sucessor ({samples} resultados, {sessions} pregões)."
            };
            SaveCycleState();
            Status = new("demo_champion", "Filtro ativo no simulador",
                $"Campeão {_demoArtifact.ModelId} continua filtrando; o sucessor será treinado automaticamente com dados novos.", samples);
            return;
        }
        _cycle = _cycle with
        {
            State = "waiting_data", UpdatedAtUtc = DateTimeOffset.UtcNow,
            Detail = $"Aguardando dados novos: {samples}/200 resultados e {sessions}/60 pregões."
        };
        SaveCycleState();
        Status = new("waiting_data", "Ciclo automático aguardando evidência",
            $"{samples}/200 resultados e {sessions}/60 pregões. Ao completar, o sucessor será treinado sozinho.", samples);
    }

    private static void WriteAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", content, new UTF8Encoding(false));
        File.Move(path + ".tmp", path, true);
    }

    /// <summary>
    /// Recalcula a evidência prospectiva depois de cada fechamento. É um relatório, nunca um comando de promoção.
    /// A comparação completa ainda exige replay de portfólio, porque pular uma entrada pode liberar outra mais tarde.
    /// </summary>
    private void UpdateProspectiveSummary()
    {
        if (_artifact is null) return;
        try
        {
            lock (_sync)
            {
                var signals = new Dictionary<string, (DateTimeOffset At, string Instrument, double P, bool Selected)>();
                var outcomes = new Dictionary<string, decimal>();
                var integrityErrors = 0;
                foreach (var file in Directory.EnumerateFiles(_directory, "eventos-*.jsonl").Order())
                foreach (var line in File.ReadLines(file).Where(l => !string.IsNullOrWhiteSpace(l)))
                {
                    try
                    {
                        using var document = JsonDocument.Parse(line);
                        var root = document.RootElement;
                        var payload = root.GetProperty("payload");
                        var expected = root.GetProperty("payloadSha256").GetString();
                        var actual = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload.GetRawText())));
                        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) { integrityErrors++; continue; }
                        var eventType = payload.GetProperty("eventType").GetString();
                        var signalId = payload.GetProperty("signalId").GetString();
                        if (string.IsNullOrWhiteSpace(signalId)) continue;
                        if (eventType == "signal"
                            && payload.TryGetProperty("modelId", out var model) && model.ValueKind == JsonValueKind.String
                            && model.GetString() == _artifact.ModelId
                            && payload.TryGetProperty("probabilityPositive", out var probability) && probability.ValueKind == JsonValueKind.Number
                            && payload.TryGetProperty("wouldSelect", out var selected) && selected.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        {
                            signals[signalId] = (payload.GetProperty("atUtc").GetDateTimeOffset(),
                                payload.GetProperty("instrument").GetString() ?? "", probability.GetDouble(), selected.GetBoolean());
                        }
                        else if (eventType == "outcome"
                                 && payload.TryGetProperty("rMultiple", out var r) && r.ValueKind == JsonValueKind.Number)
                            outcomes[signalId] = r.GetDecimal();
                    }
                    catch { integrityErrors++; }
                }

                var matched = signals.Where(s => outcomes.ContainsKey(s.Key))
                    .Select(s => (s.Value.At, s.Value.Instrument, s.Value.P, s.Value.Selected, R: outcomes[s.Key]))
                    .OrderBy(x => x.At).ToList();
                var all = Curve(matched.Select(x => x.R));
                var selectedOnly = Curve(matched.Where(x => x.Selected).Select(x => x.R));
                var prevalence = matched.Count == 0 ? 0 : matched.Count(x => x.R > 0) / (double)matched.Count;
                var brier = matched.Count == 0 ? 0 : matched.Average(x => Math.Pow(x.P - (x.R > 0 ? 1 : 0), 2));
                var sessions = matched.Select(x => DateOnly.FromDateTime(B3Clock.ToB3(x.At).DateTime)).Distinct().Count();
                var months = matched.Select(x => B3Clock.ToB3(x.At).ToString("yyyy-MM")).Distinct().Count();
                var spanDays = matched.Count < 2 ? 0 : (matched[^1].At - matched[0].At).TotalDays;
                var coverage = matched.Count == 0 ? 0 : matched.Count(x => x.Selected) / (double)matched.Count;
                var byInstrument = matched.GroupBy(x => x.Instrument).ToDictionary(g => g.Key, g => g.Count());
                var gates = new Dictionary<string, bool>
                {
                    ["integridade"] = integrityErrors == 0,
                    ["amostra_200"] = matched.Count >= 200,
                    ["60_pregoes"] = sessions >= 60,
                    ["180_dias"] = spanDays >= 180,
                    ["50_por_ativo"] = byInstrument.GetValueOrDefault("WDO") >= 50 && byInstrument.GetValueOrDefault("WIN") >= 50,
                    ["cobertura_30_a_85pct"] = coverage is >= 0.30 and <= 0.85,
                    ["media_r_melhor"] = selectedOnly.AverageR >= all.AverageR + 0.05m,
                    ["pf_melhor"] = selectedOnly.ProfitFactor > all.ProfitFactor,
                    ["drawdown_nao_pior"] = selectedOnly.MaxDrawdownR <= all.MaxDrawdownR,
                    ["brier_melhor_que_base"] = matched.Count > 0 && brier < prevalence * (1 - prevalence)
                };
                var summary = new
                {
                    schemaVersion = 1,
                    generatedAtUtc = DateTimeOffset.UtcNow,
                    modelId = _artifact.ModelId,
                    authority = "somente_sombra_sem_promocao_automatica",
                    integrityErrors,
                    samples = matched.Count,
                    sessions,
                    months,
                    spanDays,
                    coverage,
                    byInstrument,
                    baseline = all,
                    shadowSelected = selectedOnly,
                    brier,
                    climatologyBrier = prevalence * (1 - prevalence),
                    gates,
                    eligibleForHumanReview = gates.Values.All(v => v),
                    limitation = "Comparação preliminar; exige replay de portfólio e nova janela congelada antes de qualquer uso operacional."
                };
                var path = Path.Combine(_directory, "resumo-prospectivo.json");
                File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(summary, new JsonSerializerOptions(Json) { WriteIndented = true }), new UTF8Encoding(false));
                File.Move(path + ".tmp", path, true);
                var ready = gates.Values.All(v => v);
                var sampleMature = matched.Count >= 200 && sessions >= 60 && spanDays >= 180
                                   && byInstrument.GetValueOrDefault("WDO") >= 50 && byInstrument.GetValueOrDefault("WIN") >= 50;
                Status = _cycle.State is "demo_canary" or "demo_champion"
                    ? new ShadowLearningStatus(_cycle.State,
                        _cycle.State == "demo_champion" ? "Filtro ativo no simulador" : "Filtro em piloto automático no simulador",
                        _cycle.Detail, matched.Count, 50)
                    : ready
                    ? new ShadowLearningStatus("review_ready", "Filtro probabilístico aprovado para piloto",
                        $"{matched.Count} operações em {sessions} pregões e {spanDays:0} dias passaram por todos os portões automáticos.", matched.Count)
                    : sampleMature
                        ? new ShadowLearningStatus("evidence_rejected", "Filtro probabilístico não melhorou o robô",
                            $"A amostra mínima foi concluída, mas o candidato não venceu as regras atuais. Ele permanece fora das ordens automaticamente; você não precisa fazer nada.", matched.Count)
                        : new ShadowLearningStatus("collecting", "Aprendizado em sombra automático",
                            $"{matched.Count}/200 operações concluídas em {sessions}/60 pregões e {spanDays:0}/180 dias. O robô cuida disso sozinho.", matched.Count);
            }
        }
        catch (Exception ex) { WarnWrite(ex, "Falha ao atualizar o resumo prospectivo"); }
    }

    private ShadowReviewNotice? TakeReviewNotice()
    {
        if (!Status.ReviewReady || _artifact is null) return null;
        lock (_sync)
        {
            var marker = Path.Combine(_directory, $"revisao-notificada-{_artifact.ModelId}.txt");
            if (File.Exists(marker)) return null;
            File.WriteAllText(marker, DateTimeOffset.UtcNow.ToString("O"), new UTF8Encoding(false));
            return new ShadowReviewNotice("Filtro probabilístico aprovado",
                Status.Detail + " O ciclo automático o levará ao piloto no simulador e fará rollback se degradar.");
        }
    }

    private static ShadowCurve Curve(IEnumerable<decimal> values)
    {
        var list = values.ToList();
        decimal net = 0, peak = 0, drawdown = 0;
        foreach (var value in list) { net += value; peak = Math.Max(peak, net); drawdown = Math.Max(drawdown, peak - net); }
        var gain = list.Where(v => v > 0).Sum();
        var loss = -list.Where(v => v < 0).Sum();
        return new ShadowCurve(list.Count, Math.Round(net, 3), list.Count == 0 ? 0 : Math.Round(net / list.Count, 4),
            loss == 0 ? 99 : Math.Round(gain / loss, 3), Math.Round(drawdown, 3));
    }

    private sealed record ShadowEventEnvelope(string PayloadSha256, JsonElement Payload);
    private sealed record ShadowFlowContext(bool HasData, decimal Delta5s, decimal Delta30s, decimal Delta1m,
        decimal DeltaDay, decimal? BookImbalance, string Bias, bool Strong, string Absorption);
    private sealed record ShadowCurve(int Trades, decimal NetR, decimal AverageR, decimal ProfitFactor, decimal MaxDrawdownR);
    private sealed record SignalEvidence(DateTimeOffset AtUtc, string Instrument, string Setup,
        IReadOnlyList<double> Features, string? ModelId, double? Probability, bool? Selected);
    private sealed record ModelOutcome(DateTimeOffset AtUtc, string ModelId, bool Selected, decimal R);
    private sealed record TrainingEvidence(IReadOnlyList<ShadowTrainingExample> Examples,
        IReadOnlyList<ModelOutcome> ModelOutcomes, int IntegrityErrors);
    private sealed record ShadowEventPayload(
        int SchemaVersion, string EventType, string EventId, string SignalId, DateTimeOffset AtUtc,
        string Instrument, string? Setup, string? Side, string? State, string? Detail,
        string? FeatureSchemaId, IReadOnlyList<double>? Features, string ModelStatus, string? ModelId,
        double? ProbabilityPositive, bool? WouldSelect, decimal? Bid, decimal? Ask, decimal? Adr,
        ShadowFlowContext? Flow, decimal? PnlMoney, decimal? RMultiple, long? PositionId);
}
