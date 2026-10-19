namespace TradeGuard.Domain;

/// <summary>Oportunidade de entrada detectada por um setup (ver <see cref="SignalEngine"/>).</summary>
public sealed record EntrySignal(Side Side, string Setup, string Reason);
