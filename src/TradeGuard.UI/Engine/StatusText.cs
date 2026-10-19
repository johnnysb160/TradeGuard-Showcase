using TradeGuard.Domain;

namespace TradeGuard.UI.Engine;

public sealed record StatusLine(string Tone, string Headline, string? Detail);

/// <summary>O estado do robô em uma frase simples (usado no Painel e no título da janela).</summary>
public static class StatusText
{
    public static StatusLine From(EngineState s)
    {
        if (!s.Connected) return new("secondary", "MT5 desconectado", "Abra o MT5 e ligue o TradeGuard no gráfico (veja Conexão MT5).");
        if (!s.CanAct) return new("danger", "Conta real: só observando", "O robô não envia ordens em conta real enquanto isso estiver desligado em Regras.");
        if (s.Lock != LockReason.None) return new("danger", "Dia encerrado", s.Lock.Describe() + ". Volte no próximo pregão.");
        if (s.Position is not null) return new("primary", "Em operação", "O robô está cuidando do stop e do alvo.");
        if (s.ContractWarning is not null && s.Blocks.Count == 0) return new("warning", s.Settings.Mode == TradingMode.Assistant ? "Pode operar" : "Esperando sinal", s.ContractWarning);
        var block = s.Blocks.FirstOrDefault(b => b.Reason != BlockReason.ChecklistPending) ?? s.Blocks.FirstOrDefault();
        if (block is not null) return new("warning", "Aguarde", block.Message);
        // Com sinais ligados, "liberado" não é convite para entrar: a entrada é o sinal.
        return s.Settings.Mode == TradingMode.Assistant
            ? new("success", "Pode operar", null)
            : new("secondary", "Esperando sinal", "Nenhuma trava ativa. O robô avisa com som quando for a hora de entrar.");
    }
}
