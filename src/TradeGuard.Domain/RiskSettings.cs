namespace TradeGuard.Domain;

public sealed record PartialLevel(decimal TriggerPoints, int Contracts);

/// <summary>Stop em degraus: quando andar <c>TriggerPoints</c> a favor, trava <c>LockPoints</c> de lucro.</summary>
public sealed record ProfitLockLevel(decimal TriggerPoints, decimal LockPoints);

/// <summary>Quem decide a entrada. As travas e a gestão valem igual nos três modos.</summary>
public enum TradingMode
{
    /// <summary>Você clica; o robô gere e trava.</summary>
    Assistant,
    /// <summary>A estratégia sugere; você confirma com um clique.</summary>
    SemiAuto,
    /// <summary>A estratégia entra sozinha (só no simulador enquanto não houver resultado comprovado).</summary>
    Auto
}

/// <summary>Aumento de posição. Nos dois modos a perda máxima da posição inteira fica igual à da entrada inicial.</summary>
public enum ScaleMode
{
    /// <summary>Não aumenta.</summary>
    Off,
    /// <summary>Piramidação: aumenta com o trade no lucro e o fluxo a favor.</summary>
    WithTrend,
    /// <summary>Preço médio: aumenta com o trade contra, só com absorção a favor (nível de liquidez); o stop aperta.</summary>
    Average
}

public enum TrailingMode
{
    /// <summary>Segue o preço a uma distância fixa em pontos.</summary>
    Fixed,
    /// <summary>Segue a mínima (compra) / máxima (venda) do candle de 1 minuto anterior.</summary>
    PreviousCandle
}

/// <summary>
/// Todas as regras do plano. Os valores padrão são o plano inicial acordado:
/// 1 contrato, stop 5 pts, alvo 10 pts, perda diária máx. R$ 150, 4 trades/dia.
/// </summary>
public sealed record RiskSettings
{
    public TradingMode Mode { get; init; } = TradingMode.Assistant;

    // ---- Sinais (modos semi e automático) ----
    /// <summary>Setups que podem gerar sinais (ver <see cref="SetupNames"/>).</summary>
    public IReadOnlyList<string> EnabledSetups { get; init; } = SetupNames.Operational;
    /// <summary>Tocar som quando aparece um sinal.</summary>
    public bool SignalSound { get; init; } = true;
    /// <summary>Volume de todos os sons do TradeGuard (0–100), independente do volume do Windows.</summary>
    public int SoundVolumePercent { get; init; } = 30;
    /// <summary>Arquivo de C:\Windows\Media tocado ao abrir/fechar posição (vazio = sem som).</summary>
    public string EntrySound { get; init; } = "Windows Notify Messaging.wav";
    public string ExitSound { get; init; } = "Windows Proximity Notification.wav";
    /// <summary>Tape reading: rompimento só vira sinal com saldo de agressão a favor nos últimos 5 s e sem absorção contra.</summary>
    public bool FlowFilterEnabled { get; init; } = true;

    // ---- Tamanho e brackets ----
    public int Contracts { get; init; } = 1;
    /// <summary>
    /// Se &gt; 0, o número de contratos é calculado pelo risco: R$ por trade ÷ (stop × valor do ponto),
    /// limitado a <see cref="MaxContracts"/>. 0 = usa <see cref="Contracts"/> fixo.
    /// </summary>
    public decimal RiskPerTradeMoney { get; init; }
    public int MaxContracts { get; init; } = 1;
    public decimal StopPoints { get; init; } = 5m;
    /// <summary>Stop mais largo aceito se você colocar o stop à mão no MT5. Acima disso o robô aperta.</summary>
    public decimal MaxStopPoints { get; init; } = 8m;
    public decimal TargetPoints { get; init; } = 10m;

    // ---- Gestão da posição ----
    public bool BreakevenEnabled { get; init; } = true;
    public decimal BreakevenTriggerPoints { get; init; } = 5m;
    public decimal BreakevenOffsetPoints { get; init; } = 0m;
    /// <summary>Parciais só acontecem se sobrar pelo menos 1 contrato. Com 1 contrato são ignoradas.</summary>
    public IReadOnlyList<PartialLevel> Partials { get; init; } = [new PartialLevel(5m, 1)];
    public IReadOnlyList<ProfitLockLevel> ProfitLocks { get; init; } = [];
    public bool TrailingEnabled { get; init; }
    public TrailingMode TrailingMode { get; init; } = TrailingMode.Fixed;
    public decimal TrailingStartPoints { get; init; } = 8m;
    public decimal TrailingDistancePoints { get; init; } = 4m;
    public decimal TrailingStepPoints { get; init; } = 1m;
    /// <summary>Proíbe aumentar a posição (preço médio). Volume extra é zerado na hora.</summary>
    public bool AllowScaleIn { get; init; }
    /// <summary>Saída por tempo: zera se o trade não resolver em N minutos. 0 = desligado.</summary>
    public int MaxHoldMinutes { get; init; }
    public ScaleMode ScaleMode { get; init; } = ScaleMode.Off;
    /// <summary>Contratos em cada aumento.</summary>
    public int ScaleAddContracts { get; init; } = 1;
    /// <summary>Máximo de aumentos por trade (1 ou 2).</summary>
    public int ScaleMaxAdds { get; init; } = 1;
    /// <summary>Distância em pontos entre os aumentos (a favor ou contra).</summary>
    public decimal ScaleStepPoints { get; init; } = 4m;

    // ---- Limites (travas) ----
    /// <summary>
    /// Proteção do lucro do dia: depois que o dia chegar a <see cref="GiveBackActivationMoney"/>,
    /// trava se devolver este percentual do pico. 0 = desligado.
    /// </summary>
    public decimal GiveBackPercent { get; init; } = 50m;
    public decimal GiveBackActivationMoney { get; init; } = 100m;
    public decimal MaxDailyLoss { get; init; } = 150m;
    /// <summary>Opcional: bateu a meta do dia, para. 0 = desligado.</summary>
    public decimal DailyProfitTarget { get; init; }
    public decimal MaxWeeklyLoss { get; init; } = 400m;
    public decimal MaxMonthlyLoss { get; init; } = 1000m;
    public int MaxTradesPerDay { get; init; } = 4;
    public int MaxConsecutiveLosses { get; init; } = 2;
    public int CooldownAfterLossMinutes { get; init; } = 10;
    public decimal MaxSpreadPoints { get; init; } = 1.5m;

    // ---- Horários (hora de Brasília) ----
    public TimeOnly WindowStart { get; init; } = new(9, 15);
    public TimeOnly WindowEnd { get; init; } = new(17, 30);
    /// <summary>A partir daqui o robô zera tudo (evita zeragem compulsória de R$ 20/contrato).</summary>
    public TimeOnly FlattenAt { get; init; } = new(17, 45);

    // ---- Notícias ----
    public bool NewsFilterEnabled { get; init; } = true;
    public int NewsMinImportance { get; init; } = 3;
    public int NewsBlockMinutesBefore { get; init; } = 5;
    public int NewsBlockMinutesAfter { get; init; } = 5;

    // ---- Conta ----
    /// <summary>Enquanto false, o robô só opera em conta DEMO (simulador).</summary>
    public bool AllowRealAccount { get; init; }

    public static RiskSettings Default { get; } = new();

    /// <summary>Último horário possível para zerar (o pregão do mini dólar vai até 17:55).</summary>
    public static readonly TimeOnly LastFlatten = new(17, 50);

    /// <summary>Zeragem de emergência do EA (funciona mesmo com o TradeGuard fechado): 5 min depois da zeragem normal.</summary>
    public TimeOnly EmergencyFlatten => FlattenAt.AddMinutes(5) > new TimeOnly(17, 54) ? new TimeOnly(17, 54) : FlattenAt.AddMinutes(5);

    /// <summary>
    /// Lista o que ficaria mais permissivo em <paramref name="proposed"/> comparado a este.
    /// Afrouxar regras só vale a partir do próximo pregão (anti-tilt).
    /// </summary>
    public IReadOnlyList<string> LoosenedBy(RiskSettings proposed) =>
        [.. Rules.Where(r => r.Looser(this, proposed)).Select(r => r.Name)];

    /// <summary>
    /// A parte de <paramref name="proposed"/> que pode valer já: tudo que aperta ou é neutro entra,
    /// e cada campo que afrouxa volta ao valor atual (esse só entra no próximo pregão).
    /// </summary>
    public RiskSettings ApplicableNow(RiskSettings proposed) =>
        Rules.Where(r => r.Looser(this, proposed)).Aggregate(proposed, (s, r) => r.Keep(s, this));

    private sealed record Rule(string Name, Func<RiskSettings, RiskSettings, bool> Looser, Func<RiskSettings, RiskSettings, RiskSettings> Keep);

    // Looser(atual, proposto): o proposto afrouxa? Keep(proposto, atual): o proposto com o valor atual nesse campo.
    private static readonly Rule[] Rules =
    [
        // Semiautomático não é afrouxar: toda entrada continua confirmada por você. Automático é.
        new("Modo automático", (c, p) => p.Mode == TradingMode.Auto && c.Mode != TradingMode.Auto, (p, c) => p with { Mode = c.Mode }),
        new("Contratos", (c, p) => p.Contracts > c.Contracts, (p, c) => p with { Contracts = c.Contracts }),
        new("Risco por trade", (c, p) => p.RiskPerTradeMoney > c.RiskPerTradeMoney, (p, c) => p with { RiskPerTradeMoney = c.RiskPerTradeMoney }),
        new("Proteção do lucro do dia",
            (c, p) => IsLooserLimit(c.GiveBackPercent, p.GiveBackPercent) || (c.GiveBackPercent > 0 && p.GiveBackActivationMoney > c.GiveBackActivationMoney),
            (p, c) => p with { GiveBackPercent = c.GiveBackPercent, GiveBackActivationMoney = c.GiveBackActivationMoney }),
        new("Máx. contratos", (c, p) => p.MaxContracts > c.MaxContracts, (p, c) => p with { MaxContracts = c.MaxContracts }),
        new("Stop", (c, p) => p.StopPoints > c.StopPoints, (p, c) => p with { StopPoints = c.StopPoints }),
        new("Stop máximo", (c, p) => p.MaxStopPoints > c.MaxStopPoints, (p, c) => p with { MaxStopPoints = c.MaxStopPoints }),
        new("Permitir aumentar posição", (c, p) => p.AllowScaleIn && !c.AllowScaleIn, (p, c) => p with { AllowScaleIn = c.AllowScaleIn }),
        new("Perda diária", (c, p) => IsLooserLimit(c.MaxDailyLoss, p.MaxDailyLoss), (p, c) => p with { MaxDailyLoss = c.MaxDailyLoss }),
        new("Perda semanal", (c, p) => IsLooserLimit(c.MaxWeeklyLoss, p.MaxWeeklyLoss), (p, c) => p with { MaxWeeklyLoss = c.MaxWeeklyLoss }),
        new("Perda mensal", (c, p) => IsLooserLimit(c.MaxMonthlyLoss, p.MaxMonthlyLoss), (p, c) => p with { MaxMonthlyLoss = c.MaxMonthlyLoss }),
        new("Trades por dia", (c, p) => IsLooserLimit(c.MaxTradesPerDay, p.MaxTradesPerDay), (p, c) => p with { MaxTradesPerDay = c.MaxTradesPerDay }),
        new("Perdas seguidas", (c, p) => IsLooserLimit(c.MaxConsecutiveLosses, p.MaxConsecutiveLosses), (p, c) => p with { MaxConsecutiveLosses = c.MaxConsecutiveLosses }),
        new("Pausa após perda", (c, p) => p.CooldownAfterLossMinutes < c.CooldownAfterLossMinutes, (p, c) => p with { CooldownAfterLossMinutes = c.CooldownAfterLossMinutes }),
        new("Spread máximo", (c, p) => IsLooserLimit(c.MaxSpreadPoints, p.MaxSpreadPoints), (p, c) => p with { MaxSpreadPoints = c.MaxSpreadPoints }),
        new("Começar às", (c, p) => p.WindowStart < c.WindowStart, (p, c) => p with { WindowStart = c.WindowStart }),
        new("Última entrada", (c, p) => p.WindowEnd > c.WindowEnd, (p, c) => p with { WindowEnd = c.WindowEnd }),
        new("Zerar tudo às", (c, p) => p.FlattenAt > c.FlattenAt, (p, c) => p with { FlattenAt = c.FlattenAt }),
        new("Filtro de notícias", (c, p) => c.NewsFilterEnabled && !p.NewsFilterEnabled, (p, c) => p with { NewsFilterEnabled = c.NewsFilterEnabled }),
        new("Janela de notícias",
            (c, p) => p.NewsBlockMinutesBefore < c.NewsBlockMinutesBefore || p.NewsBlockMinutesAfter < c.NewsBlockMinutesAfter,
            (p, c) => p with { NewsBlockMinutesBefore = c.NewsBlockMinutesBefore, NewsBlockMinutesAfter = c.NewsBlockMinutesAfter }),
        new("Conta real", (c, p) => p.AllowRealAccount && !c.AllowRealAccount, (p, c) => p with { AllowRealAccount = c.AllowRealAccount }),
        new("Aumento de posição", (c, p) => p.ScaleMode != ScaleMode.Off && (c.ScaleMode == ScaleMode.Off || p.ScaleMode != c.ScaleMode || p.ScaleMaxAdds > c.ScaleMaxAdds || p.ScaleAddContracts > c.ScaleAddContracts),
            (p, c) => p with { ScaleMode = c.ScaleMode, ScaleMaxAdds = c.ScaleMaxAdds, ScaleAddContracts = c.ScaleAddContracts }),
        new("Filtro de fluxo", (c, p) => c.FlowFilterEnabled && !p.FlowFilterEnabled, (p, c) => p with { FlowFilterEnabled = c.FlowFilterEnabled })
    ];

    // Limite 0 significa "desligado", que é o mais permissivo possível.
    private static bool IsLooserLimit(decimal current, decimal proposed) =>
        current > 0 && (proposed == 0 || proposed > current);

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Contracts < 1) errors.Add("Contratos deve ser >= 1.");
        if (MaxContracts < Contracts) errors.Add("Máx. contratos deve ser >= Contratos.");
        if (StopPoints <= 0) errors.Add("Stop deve ser > 0. Operar sem stop não é permitido.");
        if (MaxStopPoints < StopPoints) errors.Add("Stop máximo deve ser >= Stop.");
        if (TargetPoints <= 0) errors.Add("Alvo deve ser > 0.");
        if (MaxDailyLoss <= 0) errors.Add("Perda diária máxima é obrigatória.");
        if (FlattenAt <= WindowEnd)
            errors.Add($"\"Zerar tudo às\" ({FlattenAt:HH\\:mm}) precisa ser depois da \"última entrada\" ({WindowEnd:HH\\:mm}).");
        if (FlattenAt > LastFlatten)
            errors.Add($"\"Zerar tudo às\" precisa ser até {LastFlatten:HH\\:mm}: o pregão do mini dólar termina às 17:55.");
        if (WindowStart >= WindowEnd) errors.Add("\"Começar às\" precisa ser antes da \"última entrada\".");
        if (Partials.Any(p => p.Contracts < 1 || p.TriggerPoints <= 0)) errors.Add("Parciais inválidas.");
        if (TrailingEnabled && TrailingMode == TrailingMode.Fixed && TrailingDistancePoints <= 0)
            errors.Add("Distância do trailing deve ser > 0.");
        if (ProfitLocks.Any(l => l.TriggerPoints <= 0 || l.LockPoints < 0 || l.LockPoints >= l.TriggerPoints))
            errors.Add("Degraus de stop: o lucro travado deve ser menor que o gatilho.");
        if (RiskPerTradeMoney < 0) errors.Add("Risco por trade não pode ser negativo.");
        if (ScaleMode != ScaleMode.Off)
        {
            if (ScaleMaxAdds is < 1 or > 2) errors.Add("Aumento de posição: no máximo 2 aumentos por trade.");
            if (ScaleAddContracts < 1) errors.Add("Aumento de posição: pelo menos 1 contrato por aumento.");
            if (ScaleStepPoints < 1) errors.Add("Aumento de posição: distância mínima de 1 ponto entre os aumentos.");
            if (Contracts + ScaleAddContracts * ScaleMaxAdds > MaxContracts)
                errors.Add($"Aumento de posição precisa de até {Contracts + ScaleAddContracts * ScaleMaxAdds} contratos: suba o \"Máximo de contratos\".");
        }
        if (GiveBackPercent is < 0 or > 100) errors.Add("Proteção do lucro: percentual entre 0 e 100.");
        return errors;
    }

    /// <summary>Contratos para a próxima entrada (fixo ou pelo risco em R$).</summary>
    public int ContractsFor(InstrumentSpec instrument)
    {
        if (RiskPerTradeMoney <= 0) return Math.Min(Contracts, MaxContracts);
        var perContract = instrument.Money(StopPoints, 1);
        if (perContract <= 0) return 0;
        return (int)Math.Min(MaxContracts, Math.Floor(RiskPerTradeMoney / perContract));
    }

    // ---- Presets (facilitadores) ----

    public static RiskSettings Beginner { get; } = new();

    public static RiskSettings Conservative { get; } = new()
    {
        StopPoints = 4, MaxStopPoints = 6, TargetPoints = 8, BreakevenTriggerPoints = 4,
        MaxDailyLoss = 100, MaxWeeklyLoss = 250, MaxMonthlyLoss = 600,
        MaxTradesPerDay = 3, MaxConsecutiveLosses = 2, CooldownAfterLossMinutes = 15,
        WindowStart = new(9, 30), WindowEnd = new(12, 0), FlattenAt = new(17, 45)
    };

    /// <summary>2 contratos: permite parcial de 1 e deixar 1 correr com stop em degraus.</summary>
    public static RiskSettings TwoContracts { get; } = new()
    {
        Contracts = 2, MaxContracts = 2, StopPoints = 5, MaxStopPoints = 7, TargetPoints = 15,
        Partials = [new PartialLevel(5, 1)],
        ProfitLocks = [new ProfitLockLevel(10, 5)],
        MaxDailyLoss = 200, MaxWeeklyLoss = 500, MaxMonthlyLoss = 1200
    };
}
