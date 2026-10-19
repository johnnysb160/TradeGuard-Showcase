using System.Globalization;
using TradeGuard.Domain;

namespace TradeGuard.UI.Pages;

/// <summary>Modelo mutável da tela de regras (o domínio usa record imutável).</summary>
public sealed class SettingsForm
{
    public TradingMode Mode { get; set; }
    public List<string> EnabledSetups { get; set; } = [];
    public bool SignalSound { get; set; }
    public int SoundVolumePercent { get; set; }
    public string EntrySound { get; set; } = "";
    public string ExitSound { get; set; } = "";
    public bool FlowFilterEnabled { get; set; }
    public int Contracts { get; set; }
    public decimal RiskPerTradeMoney { get; set; }
    public int MaxContracts { get; set; }
    public decimal StopPoints { get; set; }
    public decimal MaxStopPoints { get; set; }
    public decimal TargetPoints { get; set; }
    public bool AllowScaleIn { get; set; }
    public int MaxHoldMinutes { get; set; }
    public ScaleMode ScaleMode { get; set; }
    public int ScaleAddContracts { get; set; } = 1;
    public int ScaleMaxAdds { get; set; } = 1;
    public decimal ScaleStepPoints { get; set; } = 4;
    public decimal Partial3Trigger { get; set; }
    public int Partial3Contracts { get; set; } = 1;
    public decimal Partial4Trigger { get; set; }
    public int Partial4Contracts { get; set; } = 1;
    public bool BreakevenEnabled { get; set; }
    public decimal BreakevenTriggerPoints { get; set; }
    public decimal BreakevenOffsetPoints { get; set; }
    public decimal Partial1Trigger { get; set; }
    public int Partial1Contracts { get; set; } = 1;
    public decimal Partial2Trigger { get; set; }
    public int Partial2Contracts { get; set; } = 1;
    public decimal Lock1Trigger { get; set; }
    public decimal Lock1Points { get; set; }
    public decimal Lock2Trigger { get; set; }
    public decimal Lock2Points { get; set; }
    public bool TrailingEnabled { get; set; }
    public bool TrailingByCandle { get; set; }
    public decimal TrailingStartPoints { get; set; }
    public decimal TrailingDistancePoints { get; set; }
    public decimal TrailingStepPoints { get; set; }
    public decimal MaxDailyLoss { get; set; }
    public decimal DailyProfitTarget { get; set; }
    public decimal GiveBackPercent { get; set; }
    public decimal GiveBackActivationMoney { get; set; }
    public decimal MaxWeeklyLoss { get; set; }
    public decimal MaxMonthlyLoss { get; set; }
    public int MaxTradesPerDay { get; set; }
    public int MaxConsecutiveLosses { get; set; }
    public int CooldownAfterLossMinutes { get; set; }
    public decimal MaxSpreadPoints { get; set; }
    public string WindowStart { get; set; } = "";
    public string WindowEnd { get; set; } = "";
    public string FlattenAt { get; set; } = "";
    public bool NewsFilterEnabled { get; set; }
    public int NewsMinImportance { get; set; }
    public int NewsBlockMinutesBefore { get; set; }
    public int NewsBlockMinutesAfter { get; set; }
    public bool AllowRealAccount { get; set; }

    public static SettingsForm From(RiskSettings s)
    {
        var p = s.Partials.OrderBy(x => x.TriggerPoints).ToList();
        var l = s.ProfitLocks.OrderBy(x => x.TriggerPoints).ToList();
        return new SettingsForm
        {
            Mode = s.Mode, EnabledSetups = [.. s.EnabledSetups], SignalSound = s.SignalSound, SoundVolumePercent = s.SoundVolumePercent, EntrySound = s.EntrySound, ExitSound = s.ExitSound, FlowFilterEnabled = s.FlowFilterEnabled, Contracts = s.Contracts, RiskPerTradeMoney = s.RiskPerTradeMoney, MaxContracts = s.MaxContracts,
            StopPoints = s.StopPoints, MaxStopPoints = s.MaxStopPoints, TargetPoints = s.TargetPoints,
            AllowScaleIn = s.AllowScaleIn, MaxHoldMinutes = s.MaxHoldMinutes,
            ScaleMode = s.ScaleMode, ScaleAddContracts = s.ScaleAddContracts, ScaleMaxAdds = s.ScaleMaxAdds, ScaleStepPoints = s.ScaleStepPoints,
            Partial3Trigger = p.ElementAtOrDefault(2)?.TriggerPoints ?? 0, Partial3Contracts = p.ElementAtOrDefault(2)?.Contracts ?? 1,
            Partial4Trigger = p.ElementAtOrDefault(3)?.TriggerPoints ?? 0, Partial4Contracts = p.ElementAtOrDefault(3)?.Contracts ?? 1,
            BreakevenEnabled = s.BreakevenEnabled, BreakevenTriggerPoints = s.BreakevenTriggerPoints,
            BreakevenOffsetPoints = s.BreakevenOffsetPoints,
            Partial1Trigger = p.ElementAtOrDefault(0)?.TriggerPoints ?? 0, Partial1Contracts = p.ElementAtOrDefault(0)?.Contracts ?? 1,
            Partial2Trigger = p.ElementAtOrDefault(1)?.TriggerPoints ?? 0, Partial2Contracts = p.ElementAtOrDefault(1)?.Contracts ?? 1,
            Lock1Trigger = l.ElementAtOrDefault(0)?.TriggerPoints ?? 0, Lock1Points = l.ElementAtOrDefault(0)?.LockPoints ?? 0,
            Lock2Trigger = l.ElementAtOrDefault(1)?.TriggerPoints ?? 0, Lock2Points = l.ElementAtOrDefault(1)?.LockPoints ?? 0,
            TrailingEnabled = s.TrailingEnabled, TrailingByCandle = s.TrailingMode == TrailingMode.PreviousCandle,
            TrailingStartPoints = s.TrailingStartPoints, TrailingDistancePoints = s.TrailingDistancePoints,
            TrailingStepPoints = s.TrailingStepPoints,
            MaxDailyLoss = s.MaxDailyLoss, DailyProfitTarget = s.DailyProfitTarget,
            GiveBackPercent = s.GiveBackPercent, GiveBackActivationMoney = s.GiveBackActivationMoney,
            MaxWeeklyLoss = s.MaxWeeklyLoss, MaxMonthlyLoss = s.MaxMonthlyLoss, MaxTradesPerDay = s.MaxTradesPerDay,
            MaxConsecutiveLosses = s.MaxConsecutiveLosses, CooldownAfterLossMinutes = s.CooldownAfterLossMinutes,
            MaxSpreadPoints = s.MaxSpreadPoints,
            WindowStart = s.WindowStart.ToString("HH:mm"), WindowEnd = s.WindowEnd.ToString("HH:mm"),
            FlattenAt = s.FlattenAt.ToString("HH:mm"),
            NewsFilterEnabled = s.NewsFilterEnabled, NewsMinImportance = s.NewsMinImportance,
            NewsBlockMinutesBefore = s.NewsBlockMinutesBefore, NewsBlockMinutesAfter = s.NewsBlockMinutesAfter,
            AllowRealAccount = s.AllowRealAccount
        };
    }

    public bool TryBuild(out RiskSettings settings, out string error)
    {
        settings = RiskSettings.Default;
        error = "";
        if (!TryTime(WindowStart, out var ws) || !TryTime(WindowEnd, out var we) || !TryTime(FlattenAt, out var fa))
        {
            error = "Horários devem estar no formato HH:mm.";
            return false;
        }

        List<PartialLevel> partials = [];
        if (Partial1Trigger > 0) partials.Add(new(Partial1Trigger, Math.Max(1, Partial1Contracts)));
        if (Partial2Trigger > 0) partials.Add(new(Partial2Trigger, Math.Max(1, Partial2Contracts)));
        if (Partial3Trigger > 0) partials.Add(new(Partial3Trigger, Math.Max(1, Partial3Contracts)));
        if (Partial4Trigger > 0) partials.Add(new(Partial4Trigger, Math.Max(1, Partial4Contracts)));
        partials = [.. partials.OrderBy(x => x.TriggerPoints)];
        List<ProfitLockLevel> locks = [];
        if (Lock1Trigger > 0) locks.Add(new(Lock1Trigger, Lock1Points));
        if (Lock2Trigger > 0) locks.Add(new(Lock2Trigger, Lock2Points));

        settings = new RiskSettings
        {
            Mode = Mode, EnabledSetups = [.. EnabledSetups], SignalSound = SignalSound, SoundVolumePercent = Math.Clamp(SoundVolumePercent, 0, 100), EntrySound = EntrySound, ExitSound = ExitSound, FlowFilterEnabled = FlowFilterEnabled, Contracts = Contracts, RiskPerTradeMoney = RiskPerTradeMoney, MaxContracts = MaxContracts,
            StopPoints = StopPoints, MaxStopPoints = MaxStopPoints, TargetPoints = TargetPoints,
            AllowScaleIn = AllowScaleIn, MaxHoldMinutes = MaxHoldMinutes,
            ScaleMode = ScaleMode, ScaleAddContracts = ScaleAddContracts, ScaleMaxAdds = ScaleMaxAdds, ScaleStepPoints = ScaleStepPoints,
            BreakevenEnabled = BreakevenEnabled, BreakevenTriggerPoints = BreakevenTriggerPoints,
            BreakevenOffsetPoints = BreakevenOffsetPoints,
            Partials = partials, ProfitLocks = locks,
            TrailingEnabled = TrailingEnabled,
            TrailingMode = TrailingByCandle ? TrailingMode.PreviousCandle : TrailingMode.Fixed,
            TrailingStartPoints = TrailingStartPoints, TrailingDistancePoints = TrailingDistancePoints,
            TrailingStepPoints = TrailingStepPoints,
            MaxDailyLoss = MaxDailyLoss, DailyProfitTarget = DailyProfitTarget,
            GiveBackPercent = GiveBackPercent, GiveBackActivationMoney = GiveBackActivationMoney,
            MaxWeeklyLoss = MaxWeeklyLoss, MaxMonthlyLoss = MaxMonthlyLoss, MaxTradesPerDay = MaxTradesPerDay,
            MaxConsecutiveLosses = MaxConsecutiveLosses, CooldownAfterLossMinutes = CooldownAfterLossMinutes,
            MaxSpreadPoints = MaxSpreadPoints, WindowStart = ws, WindowEnd = we, FlattenAt = fa,
            NewsFilterEnabled = NewsFilterEnabled, NewsMinImportance = NewsMinImportance,
            NewsBlockMinutesBefore = NewsBlockMinutesBefore, NewsBlockMinutesAfter = NewsBlockMinutesAfter,
            AllowRealAccount = AllowRealAccount
        };
        return true;
    }

    private static bool TryTime(string s, out TimeOnly t) =>
        TimeOnly.TryParseExact(s.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out t);
}
