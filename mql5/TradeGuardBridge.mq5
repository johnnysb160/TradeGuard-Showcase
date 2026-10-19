//+------------------------------------------------------------------+
//| TradeGuardBridge.mq5                                             |
//| Ponte entre o MT5 e o app TradeGuard (.NET).                     |
//|                                                                  |
//| O EA é "burro de propósito": executa ordens e informa o estado.  |
//| Toda regra mora no .NET. Aqui só ficam proteções de último caso  |
//| que precisam funcionar mesmo com o app fechado:                  |
//|  - recusa ordem sem stop;                                        |
//|  - recusa conta real se InpAllowReal = false;                    |
//|  - põe stop de emergência se a posição ficar sem stop e sem app; |
//|  - zera tudo no horário InpHardClose.                            |
//+------------------------------------------------------------------+
#property copyright "TradeGuard"
#property version   "2.20"
#property strict

#include <Trade\Trade.mqh>

input string InpPipe              = "TradeGuard.Bridge"; // Canal do app (named pipe local, sem IP/porta)
input string InpSecret            = "";          // Chave da ponte (tela Conexão MT5)
input bool   InpAllowReal         = false;       // Permitir conta REAL
input string InpHardClose         = "17:50";     // Zeragem de emergência (HH:MM, horário de Brasília)
input int    InpB3UtcOffsetMinutes= -180;         // Offset Brasília−UTC fornecido pelo app
input double InpFailSafeStopPts   = 10.0;        // Stop de emergência se ficar sem stop e sem app (pontos)
input int    InpAppTimeoutSec     = 10;          // App sem responder por N s = desconectado
input bool   InpPushNotifications = true;        // Enviar avisos ao celular (MetaQuotes ID)
input int    InpRolloverDays      = 2;           // Troca para o próximo contrato quando faltar N dias para vencer
input int    InpHistoryDays       = 30;          // Dias de histórico enviados ao app para completar o diário
input string InpOpenCharts        = "";          // Outros ativos que este EA abre com o robô (ex.: WIN$), cada um no seu canal
input int    InpDeviationTicks    = 2;           // Desvio máximo da ordem a mercado, em ticks do ativo

#define PROTOCOL_VERSION 1
#define EA_VERSION "2.2"

// Paleta segura para quem confunde verde/amarelo: alta/alvo em AZUL, baixa/stop em VERMELHO, sem verde nem amarelo.
#define CLR_UP     C'59,158,255'
#define CLR_DOWN   C'255,90,95'
#define CLR_BG     C'11,15,20'
#define CLR_FG     C'138,151,168'
#define CLR_ENTRY  C'235,240,245'

bool     g_switching = false;         // trocando de contrato: não conecta até reiniciar no novo gráfico
bool     g_scaleFixed = false;        // escala travada por nós para mostrar stop e alvo
ulong    g_lastLevelsMs = 0;
datetime g_lastBarSent = 0;           // último candle fechado enviado ao app
ulong    g_lastStudiesMs = 0;
int      g_markCount = 0;
ulong    g_lastRolloverCheck = 0;

CTrade   g_trade;
int      g_pipe = INVALID_HANDLE;
bool     g_ready = false;             // handshake concluído
uchar    g_master[];
uchar    g_session[];
string   g_nonce = "";
long     g_outSeq = 0;
long     g_inSeq = 0;
string   g_rx = "";
ulong    g_lastRxMs = 0;
ulong    g_lastConnectTry = 0;
ulong    g_lastAcctMs = 0;
ulong    g_lastPosMs = 0;
ulong    g_lastTickMs = 0;
datetime g_lastNews = 0;
string   g_lastPosSig = "";
double   g_lastBid = 0, g_lastAsk = 0;
long     g_tradeMsc = 0;              // Times & Trades: último milissegundo enviado
int      g_tradeSameMs = 0;           // quantos negócios desse milissegundo já foram
ulong    g_lastTradesMs = 0, g_lastBookMs = 0;
bool     g_bookOn = false;
int      g_histDaysLeft = 0;          // histórico de negócios a exportar (pedido pelo app)
datetime g_histDay = 0;
int      g_histTries = 0;
ulong    g_lastHistMs = 0;
ulong    g_noStopSince = 0;
ulong    g_lastHardClose = 0;

//+------------------------------------------------------------------+
//| Crypto: SHA-256 nativo + HMAC montado à mão                      |
//+------------------------------------------------------------------+
bool Sha256(const uchar &data[], uchar &out[])
  {
   uchar key[];
   return CryptEncode(CRYPT_HASH_SHA256, data, key, out) == 32;
  }

void HmacSha256(const uchar &key[], const uchar &msg[], uchar &out[])
  {
   uchar k[64];
   ArrayInitialize(k, 0);
   if(ArraySize(key) > 64)
     {
      uchar kh[];
      Sha256(key, kh);
      ArrayCopy(k, kh, 0, 0, 32);
     }
   else
      ArrayCopy(k, key, 0, 0, ArraySize(key));

   int n = ArraySize(msg);
   uchar inner[];
   ArrayResize(inner, 64 + n);
   for(int i = 0; i < 64; i++) inner[i] = (uchar)(k[i] ^ 0x36);
   if(n > 0) ArrayCopy(inner, msg, 64, 0, n);
   uchar ih[];
   Sha256(inner, ih);

   uchar outer[];
   ArrayResize(outer, 64 + 32);
   for(int i = 0; i < 64; i++) outer[i] = (uchar)(k[i] ^ 0x5c);
   ArrayCopy(outer, ih, 64, 0, 32);
   Sha256(outer, out);
  }

void Utf8(const string s, uchar &out[])
  {
   int n = StringToCharArray(s, out, 0, -1, CP_UTF8);
   ArrayResize(out, MathMax(0, n - 1)); // remove o \0 final
  }

string ToHex(const uchar &b[])
  {
   string h = "";
   for(int i = 0; i < ArraySize(b); i++) h += StringFormat("%02x", b[i]);
   return h;
  }

bool FromHex(const string hex, uchar &out[])
  {
   string s = hex;
   StringTrimLeft(s);
   StringTrimRight(s);
   StringToLower(s);
   int n = StringLen(s);
   if(n == 0 || n % 2 != 0) return false;
   ArrayResize(out, n / 2);
   for(int i = 0; i < n / 2; i++)
     {
      int hi = HexVal(StringGetCharacter(s, 2 * i));
      int lo = HexVal(StringGetCharacter(s, 2 * i + 1));
      if(hi < 0 || lo < 0) return false;
      out[i] = (uchar)(hi * 16 + lo);
     }
   return true;
  }

int HexVal(ushort c)
  {
   if(c >= '0' && c <= '9') return c - '0';
   if(c >= 'a' && c <= 'f') return c - 'a' + 10;
   return -1;
  }

string Mac(const string json, const uchar &key[])
  {
   uchar msg[], mac[];
   Utf8(json, msg);
   HmacSha256(key, msg, mac);
   return ToHex(mac);
  }

string RandomNonce()
  {
   string s = "";
   for(int i = 0; i < 16; i++) s += StringFormat("%02x", MathRand() % 256);
   return s + StringFormat("%I64x", GetMicrosecondCount());
  }

//+------------------------------------------------------------------+
//| JSON mínimo (o .NET só manda objetos planos string/número)       |
//+------------------------------------------------------------------+
string JStr(const string json, const string key)
  {
   string pat = "\"" + key + "\":\"";
   int p = StringFind(json, pat);
   if(p < 0) return "";
   p += StringLen(pat);
   int e = StringFind(json, "\"", p);
   return e < 0 ? "" : StringSubstr(json, p, e - p);
  }

double JNum(const string json, const string key)
  {
   string pat = "\"" + key + "\":";
   int p = StringFind(json, pat);
   if(p < 0) return 0;
   p += StringLen(pat);
   int e = p;
   int n = StringLen(json);
   while(e < n)
     {
      ushort c = StringGetCharacter(json, e);
      if(c == ',' || c == '}') break;
      e++;
     }
   return StringToDouble(StringSubstr(json, p, e - p));
  }

string Esc(const string s)
  {
   string r = "";
   for(int i = 0; i < StringLen(s); i++)
     {
      ushort c = StringGetCharacter(s, i);
      if(c == '"' || c == '\\' || c < 32) continue; // acentos passam (a ponte é UTF-8)
      r += ShortToString(c);
     }
   return r;
  }

string D(double v, int digits = -1)
  {
   if(digits < 0) digits = (int)SymbolInfoInteger(_Symbol, SYMBOL_DIGITS);
   return DoubleToString(v, digits);
  }

//+------------------------------------------------------------------+
//| Transporte                                                       |
//+------------------------------------------------------------------+
// Named pipe aberto como arquivo: não precisa de lista de WebRequest nem de DLL.
string PipePath() { return "\\\\.\\pipe\\" + InpPipe; }

bool SendRaw(const string frame)
  {
   if(g_pipe == INVALID_HANDLE) return false;
   uchar buf[];
   Utf8(frame, buf);
   ResetLastError();
   uint written = FileWriteArray(g_pipe, buf, 0, ArraySize(buf));
   if(written != (uint)ArraySize(buf))
     {
      Disconnect("falha ao enviar (" + IntegerToString(GetLastError()) + ")");
      return false;
     }
   FileFlush(g_pipe);
   return true;
  }

// body sem "{" inicial e sem "}" final, ex.: "\"t\":\"tick\",\"bid\":1"
bool Send(const string type, const string fields)
  {
   if(!g_ready) return false;
   g_outSeq++;
   string json = "{\"t\":\"" + type + "\",\"seq\":" + IntegerToString(g_outSeq) + (fields == "" ? "" : "," + fields) + "}";
   return SendRaw(json + "\t" + Mac(json, g_session) + "\n");
  }

void Disconnect(const string reason)
  {
   if(g_pipe != INVALID_HANDLE)
     {
      FileClose(g_pipe);
      g_pipe = INVALID_HANDLE;
     }
   if(g_ready) Print("TradeGuard: desconectado (", reason, ")");
   g_ready = false;
   g_rx = "";
  }

void TryConnect()
  {
   if(g_pipe != INVALID_HANDLE) return;
   ulong now = GetTickCount64();
   if(now - g_lastConnectTry < 2000) return;
   g_lastConnectTry = now;

   // Falha aqui só significa que o app TradeGuard não está aberto (ou ocupado): tenta de novo em 2 s.
   g_pipe = FileOpen(PipePath(), FILE_READ | FILE_WRITE | FILE_BIN | FILE_SHARE_READ | FILE_SHARE_WRITE);
   if(g_pipe == INVALID_HANDLE) return;

   g_nonce = RandomNonce();
   g_outSeq = 0;
   g_inSeq = 0;
   g_rx = "";
   string json = "{\"t\":\"hello\",\"seq\":0,\"v\":" + IntegerToString(PROTOCOL_VERSION)
                 + ",\"ver\":\"" + EA_VERSION + "\""
                 + ",\"nonce\":\"" + g_nonce + "\""
                 + ",\"acct\":" + IntegerToString(AccountInfoInteger(ACCOUNT_LOGIN))
                 + ",\"server\":\"" + Esc(AccountInfoString(ACCOUNT_SERVER)) + "\""
                 + ",\"company\":\"" + Esc(AccountInfoString(ACCOUNT_COMPANY)) + "\""
                 + ",\"demo\":" + (IsDemo() ? "1" : "0")
                 + ",\"netting\":" + (AccountInfoInteger(ACCOUNT_MARGIN_MODE) != ACCOUNT_MARGIN_MODE_RETAIL_HEDGING ? "1" : "0")
                 + ",\"sym\":\"" + Esc(_Symbol) + "\""
                 + ",\"tick\":" + D(SymbolInfoDouble(_Symbol, SYMBOL_TRADE_TICK_SIZE), 8)
                 + ",\"tickval\":" + D(SymbolInfoDouble(_Symbol, SYMBOL_TRADE_TICK_VALUE), 8)
                 + ",\"digits\":" + IntegerToString(SymbolInfoInteger(_Symbol, SYMBOL_DIGITS))
                 + ",\"exp\":" + IntegerToString((long)SymbolInfoInteger(_Symbol, SYMBOL_EXPIRATION_TIME))
                 + ",\"srv\":" + IntegerToString((long)TimeTradeServer())
                 + ",\"gmt\":" + IntegerToString((long)TimeGMT())
                 + "}";
   SendRaw(json + "\t" + Mac(json, g_master) + "\n");
   g_lastRxMs = GetTickCount64();
  }

void ReadIncoming()
  {
   if(g_pipe == INVALID_HANDLE) return;
   // Em pipe, FileSize devolve quantos bytes estão esperando leitura (não bloqueia).
   ResetLastError();
   ulong avail = FileSize(g_pipe);
   if(GetLastError() != 0) { Disconnect("app fechou o canal"); return; }
   if(avail > 0)
     {
      uchar buf[];
      uint n = FileReadArray(g_pipe, buf, 0, (int)avail);
      if(n > 0) g_rx += CharArrayToString(buf, 0, (int)n, CP_UTF8);
     }

   int nl;
   while((nl = StringFind(g_rx, "\n")) >= 0)
     {
      string line = StringSubstr(g_rx, 0, nl);
      g_rx = StringSubstr(g_rx, nl + 1);
      HandleLine(line);
      if(g_pipe == INVALID_HANDLE) return;
     }
  }

void HandleLine(const string line)
  {
   int tab = StringFind(line, "\t");
   if(tab <= 0) { Disconnect("linha inválida"); return; }
   string json = StringSubstr(line, 0, tab);
   string mac = StringSubstr(line, tab + 1);
   StringTrimRight(mac);

   if(!g_ready)
     {
      if(mac != Mac(json, g_master) || JStr(json, "t") != "welcome")
        {
         Disconnect("chave inválida (confira InpSecret)");
         return;
        }
      uchar seed[];
      Utf8("tg-session|" + g_nonce + "|" + JStr(json, "nonce"), seed);
      HmacSha256(g_master, seed, g_session);
      g_ready = true;
      g_lastRxMs = GetTickCount64();
      g_lastPosSig = "";
      g_lastNews = 0;
      Print("TradeGuard: conectado ao app.");
      SendPosition(true);
      SendAccount();
      SendHistory();
      SendBars(true);
      g_tradeMsc = 0;
      SendFlowCaps();
      return;
     }

   if(mac != Mac(json, g_session)) { Disconnect("assinatura inválida"); return; }
   long seq = (long)JNum(json, "seq");
   if(seq <= g_inSeq) { Disconnect("sequência inválida"); return; }
   g_inSeq = seq;
   g_lastRxMs = GetTickCount64();

   string t = JStr(json, "t");
   if(t == "open") DoOpen(json);
   else if(t == "modify") DoModify(json);
   else if(t == "reduce") DoReduce(json);
   else if(t == "closeall") DoCloseAll(JStr(json, "id"), JStr(json, "reason"));
   else if(t == "notify") Notify(JStr(json, "msg"));
   else if(t == "mark") Mark(JStr(json, "side") == "buy", JStr(json, "text"));
   else if(t == "barhist") ExportBarHistory(JStr(json, "sym"), (int)JNum(json, "days"));
   else if(t == "newshist") ExportNewsHistory((int)JNum(json, "days"));
   else if(t == "tickhist") { g_histDaysLeft = (int)JNum(json, "days"); g_histDay = TimeTradeServer() - 86400; g_histTries = 0; }
   // "hb" só atualiza g_lastRxMs
  }

//+------------------------------------------------------------------+
//| Execução                                                         |
//+------------------------------------------------------------------+
bool IsDemo() { return AccountInfoInteger(ACCOUNT_TRADE_MODE) == ACCOUNT_TRADE_MODE_DEMO; }

bool TradingAllowedHere(string &why)
  {
   if(!IsDemo() && !InpAllowReal) { why = "conta real bloqueada no EA (InpAllowReal)"; return false; }
   if(!TerminalInfoInteger(TERMINAL_TRADE_ALLOWED)) { why = "Algotrading desligado no MT5"; return false; }
   return true;
  }

double NormPrice(double p)
  {
   double tick = SymbolInfoDouble(_Symbol, SYMBOL_TRADE_TICK_SIZE);
   if(tick <= 0) return p;
   return NormalizeDouble(MathRound(p / tick) * tick, (int)SymbolInfoInteger(_Symbol, SYMBOL_DIGITS));
  }

void Ack(const string id, bool ok, const string msg)
  {
   uint ret = g_trade.ResultRetcode();
   Send("ack", "\"id\":\"" + id + "\",\"ok\":" + (ok ? "1" : "0") + ",\"ret\":" + IntegerToString(ret)
        + ",\"msg\":\"" + Esc(msg) + "\"");
  }

void DoOpen(const string json)
  {
   string id = JStr(json, "id");
   string why;
   if(!TradingAllowedHere(why)) { Ack(id, false, why); return; }

   double sl = NormPrice(JNum(json, "sl"));
   double tp = NormPrice(JNum(json, "tp"));
   double vol = JNum(json, "vol");
   if(sl <= 0) { Ack(id, false, "ordem sem stop recusada"); return; }
   if(vol <= 0) { Ack(id, false, "volume inválido"); return; }

   string comment = JStr(json, "comment");
   if(comment == "") comment = "TradeGuard";
   bool ok = JStr(json, "side") == "buy"
             ? g_trade.Buy(vol, _Symbol, 0, sl, tp, comment)
             : g_trade.Sell(vol, _Symbol, 0, sl, tp, comment);
   Ack(id, ok && IsDone(), g_trade.ResultRetcodeDescription());
   // A posição é enviada por OnTradeTransaction, depois do negócio (o app precisa do negócio antes do "zerado").
  }

void DoModify(const string json)
  {
   string id = JStr(json, "id");
   if(!PositionSelect(_Symbol)) { Ack(id, false, "sem posição"); return; }
   double sl = NormPrice(JNum(json, "sl"));
   double tp = NormPrice(JNum(json, "tp"));
   if(sl <= 0) { Ack(id, false, "remover o stop não é permitido"); return; }
   bool ok = g_trade.PositionModify(_Symbol, sl, tp);
   Ack(id, ok && IsDone(), g_trade.ResultRetcodeDescription());
   SendPosition(true);
  }

void DoReduce(const string json)
  {
   string id = JStr(json, "id");
   if(!PositionSelect(_Symbol)) { Ack(id, false, "sem posição"); return; }
   double vol = MathMin(JNum(json, "vol"), PositionGetDouble(POSITION_VOLUME));
   if(vol <= 0) { Ack(id, false, "volume inválido"); return; }
   // Netting: reduzir = ordem contrária a mercado (o stop/alvo da posição restante continuam).
   bool isBuy = PositionGetInteger(POSITION_TYPE) == POSITION_TYPE_BUY;
   string comment = "TG " + JStr(json, "reason");
   bool ok = isBuy ? g_trade.Sell(vol, _Symbol, 0, 0, 0, comment) : g_trade.Buy(vol, _Symbol, 0, 0, 0, comment);
   Ack(id, ok && IsDone(), g_trade.ResultRetcodeDescription());
   // A posição é enviada por OnTradeTransaction, depois do negócio (o app precisa do negócio antes do "zerado").
  }

void DoCloseAll(const string id, const string reason)
  {
   if(!PositionSelect(_Symbol)) { if(id != "") Ack(id, true, "já zerado"); return; }
   g_trade.SetExpertMagicNumber(0);
   bool ok = g_trade.PositionClose(_Symbol);
   if(id != "") Ack(id, ok && IsDone(), g_trade.ResultRetcodeDescription());
   if(ok) Print("TradeGuard: posição zerada (", reason, ")");
   // A posição é enviada por OnTradeTransaction, depois do negócio (o app precisa do negócio antes do "zerado").
  }

bool IsDone()
  {
   uint r = g_trade.ResultRetcode();
   return r == TRADE_RETCODE_DONE || r == TRADE_RETCODE_DONE_PARTIAL || r == TRADE_RETCODE_PLACED;
  }

void Notify(const string msg)
  {
   if(msg == "") return;
   Print(msg);
   if(InpPushNotifications && TerminalInfoInteger(TERMINAL_NOTIFICATIONS_ENABLED))
      SendNotification(msg);
  }

//+------------------------------------------------------------------+
//| Estado → app                                                     |
//+------------------------------------------------------------------+
void SendPosition(bool force)
  {
   string side = "flat";
   double vol = 0, price = 0, sl = 0, tp = 0, profit = 0;
   long pid = 0, opened = 0;
   string comment = "";
   if(PositionSelect(_Symbol))
     {
      side = PositionGetInteger(POSITION_TYPE) == POSITION_TYPE_BUY ? "buy" : "sell";
      vol = PositionGetDouble(POSITION_VOLUME);
      price = PositionGetDouble(POSITION_PRICE_OPEN);
      sl = PositionGetDouble(POSITION_SL);
      tp = PositionGetDouble(POSITION_TP);
      profit = PositionGetDouble(POSITION_PROFIT);
      pid = PositionGetInteger(POSITION_IDENTIFIER);
      opened = PositionGetInteger(POSITION_TIME);
      comment = PositionGetString(POSITION_COMMENT);
     }
   string sig = side + D(vol, 0) + D(price) + D(sl) + D(tp);
   if(!force && sig == g_lastPosSig && GetTickCount64() - g_lastPosMs < 1000) return;
   g_lastPosSig = sig;
   g_lastPosMs = GetTickCount64();
   Send("pos", "\"side\":\"" + side + "\",\"vol\":" + D(vol, 0) + ",\"price\":" + D(price) + ",\"sl\":" + D(sl)
        + ",\"tp\":" + D(tp) + ",\"profit\":" + D(profit, 2) + ",\"pid\":" + IntegerToString(pid)
        + ",\"opened\":" + IntegerToString(opened) + ",\"comment\":\"" + Esc(comment) + "\"");
  }

void SendAccount()
  {
   g_lastAcctMs = GetTickCount64();
   Send("acct", "\"balance\":" + D(AccountInfoDouble(ACCOUNT_BALANCE), 2)
        + ",\"equity\":" + D(AccountInfoDouble(ACCOUNT_EQUITY), 2)
        + ",\"free\":" + D(AccountInfoDouble(ACCOUNT_MARGIN_FREE), 2)
        + ",\"srv\":" + IntegerToString((long)TimeTradeServer())
        + ",\"gmt\":" + IntegerToString((long)TimeGMT())
        + ",\"conn\":" + (TerminalInfoInteger(TERMINAL_CONNECTED) ? "1" : "0")
        + ",\"algo\":" + (TerminalInfoInteger(TERMINAL_TRADE_ALLOWED) && MQLInfoInteger(MQL_TRADE_ALLOWED) ? "1" : "0"));
  }

//+------------------------------------------------------------------+
//| Visual do gráfico (aplicado sempre que o EA inicia)              |
//+------------------------------------------------------------------+
void ApplyChartStyle()
  {
   ChartSetInteger(0, CHART_MODE, CHART_CANDLES);
   ChartSetInteger(0, CHART_COLOR_BACKGROUND, CLR_BG);
   ChartSetInteger(0, CHART_COLOR_FOREGROUND, CLR_FG);
   ChartSetInteger(0, CHART_SHOW_GRID, false);
   ChartSetInteger(0, CHART_COLOR_CHART_UP, CLR_UP);
   ChartSetInteger(0, CHART_COLOR_CANDLE_BULL, CLR_UP);
   ChartSetInteger(0, CHART_COLOR_CHART_DOWN, CLR_DOWN);
   ChartSetInteger(0, CHART_COLOR_CANDLE_BEAR, CLR_DOWN);
   ChartSetInteger(0, CHART_COLOR_CHART_LINE, CLR_FG);
   ChartSetInteger(0, CHART_COLOR_VOLUME, CLR_FG);
   ChartSetInteger(0, CHART_COLOR_BID, C'90,100,115');
   ChartSetInteger(0, CHART_COLOR_ASK, C'90,100,115');
   ChartSetInteger(0, CHART_COLOR_LAST, C'90,100,115');
   ChartSetInteger(0, CHART_COLOR_STOP_LEVEL, CLR_DOWN);
   ChartSetInteger(0, CHART_SHOW_TRADE_LEVELS, true);
   ChartSetInteger(0, CHART_SHIFT, true);          // espaço à direita para os nomes das linhas
   ChartSetDouble(0, CHART_SHIFT_SIZE, 18);
   ChartSetInteger(0, CHART_SCALE, 3);
   ChartRedraw();
  }

string Brl(double v) { return (v >= 0 ? "+R$ " : "-R$ ") + DoubleToString(MathAbs(v), 0); }

string Px(double p)
  {
   string s = DoubleToString(p, 1);
   StringReplace(s, ".", ",");
   return s;
  }

void Level(const string name, double price, color clr, int width, const string text)
  {
   if(ObjectFind(0, name) < 0) ObjectCreate(0, name, OBJ_HLINE, 0, 0, price);
   ObjectSetDouble(0, name, OBJPROP_PRICE, price);
   ObjectSetInteger(0, name, OBJPROP_COLOR, clr);
   ObjectSetInteger(0, name, OBJPROP_WIDTH, width);
   ObjectSetInteger(0, name, OBJPROP_STYLE, STYLE_SOLID);
   ObjectSetInteger(0, name, OBJPROP_SELECTABLE, false);
   ObjectSetInteger(0, name, OBJPROP_HIDDEN, true);

   string lbl = name + "_T";
   datetime t = TimeCurrent() + PeriodSeconds() * 2;
   if(ObjectFind(0, lbl) < 0) ObjectCreate(0, lbl, OBJ_TEXT, 0, t, price);
   ObjectMove(0, lbl, 0, t, price);
   ObjectSetString(0, lbl, OBJPROP_TEXT, text);
   ObjectSetString(0, lbl, OBJPROP_FONT, "Segoe UI Semibold");
   ObjectSetInteger(0, lbl, OBJPROP_FONTSIZE, 11);
   ObjectSetInteger(0, lbl, OBJPROP_COLOR, clr);
   ObjectSetInteger(0, lbl, OBJPROP_ANCHOR, ANCHOR_LEFT_LOWER);
   ObjectSetInteger(0, lbl, OBJPROP_SELECTABLE, false);
   ObjectSetInteger(0, lbl, OBJPROP_HIDDEN, true);
  }

void RemoveLevel(const string name)
  {
   ObjectDelete(0, name);
   ObjectDelete(0, name + "_T");
  }

//+------------------------------------------------------------------+
//| Candles para o app (os setups são calculados lá)                 |
//+------------------------------------------------------------------+
void SendBar(const MqlRates &r, bool hist)
  {
   Send("bar", "\"time\":" + IntegerToString((long)r.time)
        + ",\"o\":" + D(r.open) + ",\"h\":" + D(r.high) + ",\"l\":" + D(r.low) + ",\"c\":" + D(r.close)
        + ",\"sec\":" + IntegerToString(PeriodSeconds()) + ",\"hist\":" + (hist ? "1" : "0"));
  }

// history=true: últimos 400 candles fechados (ao conectar). Depois, cada candle que fecha.
void SendBars(bool history)
  {
   MqlRates rates[];
   ArraySetAsSeries(rates, false);
   int want = history ? 400 : 3;
   int got = CopyRates(_Symbol, PERIOD_CURRENT, 1, want, rates); // shift 1 = só candles fechados
   if(got <= 0) return;
   for(int i = 0; i < got; i++)
     {
      if(!history && rates[i].time <= g_lastBarSent) continue;
      SendBar(rates[i], history);
     }
   g_lastBarSent = rates[got - 1].time;
   if(history) Send("barsend", "");
  }

//+------------------------------------------------------------------+
//| Estudos no gráfico: médias e referências do dia (com nome)       |
//+------------------------------------------------------------------+
#define CLR_MA_FAST C'139,124,255'   // violeta: MME 9
#define CLR_MA_SLOW C'235,240,245'   // branco: MM 21
#define CLR_REF     C'150,160,175'   // cinza: referências do dia

void Seg(const string name, datetime t1, double p1, datetime t2, double p2, color clr, int width)
  {
   if(ObjectFind(0, name) < 0) ObjectCreate(0, name, OBJ_TREND, 0, t1, p1, t2, p2);
   ObjectMove(0, name, 0, t1, p1);
   ObjectMove(0, name, 1, t2, p2);
   ObjectSetInteger(0, name, OBJPROP_COLOR, clr);
   ObjectSetInteger(0, name, OBJPROP_WIDTH, width);
   ObjectSetInteger(0, name, OBJPROP_RAY_RIGHT, false);
   ObjectSetInteger(0, name, OBJPROP_SELECTABLE, false);
   ObjectSetInteger(0, name, OBJPROP_HIDDEN, true);
   ObjectSetInteger(0, name, OBJPROP_BACK, true);
  }

void Tag(const string name, datetime t, double p, const string text, color clr)
  {
   if(ObjectFind(0, name) < 0) ObjectCreate(0, name, OBJ_TEXT, 0, t, p);
   ObjectMove(0, name, 0, t, p);
   ObjectSetString(0, name, OBJPROP_TEXT, text);
   ObjectSetString(0, name, OBJPROP_FONT, "Segoe UI Semibold");
   ObjectSetInteger(0, name, OBJPROP_FONTSIZE, 9);
   ObjectSetInteger(0, name, OBJPROP_COLOR, clr);
   ObjectSetInteger(0, name, OBJPROP_ANCHOR, ANCHOR_LEFT);
   ObjectSetInteger(0, name, OBJPROP_SELECTABLE, false);
   ObjectSetInteger(0, name, OBJPROP_HIDDEN, true);
  }

void RefLine(const string name, double price, const string text)
  {
   if(price <= 0) { ObjectDelete(0, name); ObjectDelete(0, name + "_T"); return; }
   if(ObjectFind(0, name) < 0) ObjectCreate(0, name, OBJ_HLINE, 0, 0, price);
   ObjectSetDouble(0, name, OBJPROP_PRICE, price);
   ObjectSetInteger(0, name, OBJPROP_COLOR, CLR_REF);
   ObjectSetInteger(0, name, OBJPROP_STYLE, STYLE_DOT);
   ObjectSetInteger(0, name, OBJPROP_WIDTH, 1);
   ObjectSetInteger(0, name, OBJPROP_SELECTABLE, false);
   ObjectSetInteger(0, name, OBJPROP_HIDDEN, true);
   ObjectSetInteger(0, name, OBJPROP_BACK, true);
   Tag(name + "_T", TimeCurrent() + PeriodSeconds() * 2, price, text + " " + Px(price), CLR_REF);
  }

double MaAt(const double &closes[], int idx, int period, bool exponential, double prev)
  {
   if(!exponential)
     {
      if(idx < period - 1) return 0;
      double s = 0;
      for(int k = idx - period + 1; k <= idx; k++) s += closes[k];
      return s / period;
     }
   double a = 2.0 / (period + 1);
   return idx == 0 ? closes[0] : closes[idx] * a + prev * (1 - a);
  }

void DrawStudies()
  {
   g_lastStudiesMs = GetTickCount64();
   int bars = 150;
   MqlRates r[];
   ArraySetAsSeries(r, false);
   int got = CopyRates(_Symbol, PERIOD_CURRENT, 0, bars + 30, r);
   if(got < 40) return;

   double closes[];
   ArrayResize(closes, got);
   for(int i = 0; i < got; i++) closes[i] = r[i].close;

   double ema[], sma[];
   ArrayResize(ema, got);
   ArrayResize(sma, got);
   for(int i = 0; i < got; i++)
     {
      ema[i] = MaAt(closes, i, 9, true, i > 0 ? ema[i - 1] : 0);
      sma[i] = MaAt(closes, i, 21, false, 0);
     }
   int from = MathMax(21, got - bars);
   for(int i = from; i < got; i++)
     {
      Seg("TG_MA9_" + IntegerToString(i - from), r[i - 1].time, ema[i - 1], r[i].time, ema[i], CLR_MA_FAST, 2);
      Seg("TG_MA21_" + IntegerToString(i - from), r[i - 1].time, sma[i - 1], r[i].time, sma[i], CLR_MA_SLOW, 3);
     }
   datetime tagTime = TimeCurrent() + PeriodSeconds() * 2;
   Tag("TG_MA9_T", tagTime, ema[got - 1], "MÉDIA RÁPIDA (9)", CLR_MA_FAST);
   Tag("TG_MA21_T", tagTime, sma[got - 1], "MÉDIA LENTA (21) = TENDÊNCIA", CLR_MA_SLOW);

   // Leitura do mercado (mesma regra do app): candle fechado vs. média lenta e inclinação dela.
   int k = got - 2;
   int trend = 0;
   if(k >= 26 && sma[k] > sma[k - 5] && closes[k] > sma[k]) trend = 1;
   else if(k >= 26 && sma[k] < sma[k - 5] && closes[k] < sma[k]) trend = -1;
   DrawLegend(trend);

   // Referências do dia (candles diários da B3).
   RefLine("TG_DAYH", iHigh(_Symbol, PERIOD_D1, 0), "MÁX DIA");
   RefLine("TG_DAYL", iLow(_Symbol, PERIOD_D1, 0), "MÍN DIA");
   RefLine("TG_DAYO", iOpen(_Symbol, PERIOD_D1, 0), "ABERTURA");
   RefLine("TG_PREVC", iClose(_Symbol, PERIOD_D1, 1), "FECH. ONTEM");
   ChartRedraw();
  }

void Panel(const string name, int y, const string text, int size, color clr, bool bold)
  {
   if(ObjectFind(0, name) < 0) ObjectCreate(0, name, OBJ_LABEL, 0, 0, 0);
   ObjectSetInteger(0, name, OBJPROP_CORNER, CORNER_LEFT_UPPER);
   ObjectSetInteger(0, name, OBJPROP_XDISTANCE, 16);
   ObjectSetInteger(0, name, OBJPROP_YDISTANCE, y);
   ObjectSetString(0, name, OBJPROP_TEXT, text);
   ObjectSetString(0, name, OBJPROP_FONT, bold ? "Segoe UI Semibold" : "Segoe UI");
   ObjectSetInteger(0, name, OBJPROP_FONTSIZE, size);
   ObjectSetInteger(0, name, OBJPROP_COLOR, clr);
   ObjectSetInteger(0, name, OBJPROP_SELECTABLE, false);
   ObjectSetInteger(0, name, OBJPROP_HIDDEN, true);
  }

// Quadro fixo no canto: a leitura do mercado em palavras + o que cada linha significa.
void DrawLegend(int trend)
  {
   string bg = "TG_LEG_BG";
   if(ObjectFind(0, bg) < 0) ObjectCreate(0, bg, OBJ_RECTANGLE_LABEL, 0, 0, 0);
   ObjectSetInteger(0, bg, OBJPROP_CORNER, CORNER_LEFT_UPPER);
   ObjectSetInteger(0, bg, OBJPROP_XDISTANCE, 6);
   ObjectSetInteger(0, bg, OBJPROP_YDISTANCE, 22);
   ObjectSetInteger(0, bg, OBJPROP_XSIZE, 430);
   ObjectSetInteger(0, bg, OBJPROP_YSIZE, 122);
   ObjectSetInteger(0, bg, OBJPROP_BGCOLOR, C'17,22,29');
   ObjectSetInteger(0, bg, OBJPROP_BORDER_TYPE, BORDER_FLAT);
   ObjectSetInteger(0, bg, OBJPROP_COLOR, C'46,58,72');
   ObjectSetInteger(0, bg, OBJPROP_SELECTABLE, false);
   ObjectSetInteger(0, bg, OBJPROP_HIDDEN, true);

   string title = trend > 0 ? "TENDÊNCIA: ALTA ▲   procure só COMPRAS"
                : trend < 0 ? "TENDÊNCIA: BAIXA ▼   procure só VENDAS"
                : "TENDÊNCIA: LATERAL ↔   sem direção, espere";
   color tc = trend > 0 ? CLR_UP : trend < 0 ? CLR_DOWN : CLR_FG;
   Panel("TG_LEG_1", 30, title, 14, tc, true);
   Panel("TG_LEG_2", 62, "━  Linha BRANCA grossa = média lenta (21): a tendência", 10, CLR_MA_SLOW, false);
   Panel("TG_LEG_3", 82, "━  Linha VIOLETA = média rápida (9): o movimento de agora", 10, CLR_MA_FAST, false);
   Panel("TG_LEG_4", 102, "┄  Pontilhadas cinza = máxima, mínima e abertura do dia", 10, CLR_REF, false);
  }

// Seta do sinal (enviada pelo app) com o nome do setup.
void Mark(bool isBuy, const string text)
  {
   g_markCount++;
   string name = "TG_SIG_" + IntegerToString(g_markCount);
   datetime t = iTime(_Symbol, PERIOD_CURRENT, 0);
   double p = isBuy ? iLow(_Symbol, PERIOD_CURRENT, 0) : iHigh(_Symbol, PERIOD_CURRENT, 0);
   ObjectCreate(0, name, isBuy ? OBJ_ARROW_UP : OBJ_ARROW_DOWN, 0, t, p);
   ObjectSetInteger(0, name, OBJPROP_COLOR, isBuy ? CLR_UP : CLR_DOWN);
   ObjectSetInteger(0, name, OBJPROP_WIDTH, 4);
   ObjectSetInteger(0, name, OBJPROP_ANCHOR, isBuy ? ANCHOR_TOP : ANCHOR_BOTTOM);
   ObjectSetInteger(0, name, OBJPROP_SELECTABLE, false);
   Tag(name + "_T", t, p, "  " + text, isBuy ? CLR_UP : CLR_DOWN);
   ChartRedraw();
  }

// Só as linhas da posição (médias, referências e setas de sinal ficam).
void ClearLevels()
  {
   RemoveLevel("TG_ENTRY");
   RemoveLevel("TG_STOP");
   RemoveLevel("TG_TARGET");
   if(g_scaleFixed)
     {
      ChartSetInteger(0, CHART_SCALEFIX, false);
      g_scaleFixed = false;
     }
  }

// Linhas grossas, com nome e valor em reais. Enquanto há posição, a escala se ajusta para stop e alvo aparecerem.
void DrawLevels()
  {
   g_lastLevelsMs = GetTickCount64();
   if(!PositionSelect(_Symbol)) { ClearLevels(); ChartRedraw(); return; }

   bool isBuy = PositionGetInteger(POSITION_TYPE) == POSITION_TYPE_BUY;
   double sign = isBuy ? 1 : -1;
   double vol = PositionGetDouble(POSITION_VOLUME);
   double entry = PositionGetDouble(POSITION_PRICE_OPEN);
   double sl = PositionGetDouble(POSITION_SL);
   double tp = PositionGetDouble(POSITION_TP);
   double tick = SymbolInfoDouble(_Symbol, SYMBOL_TRADE_TICK_SIZE);
   double pv = tick > 0 ? SymbolInfoDouble(_Symbol, SYMBOL_TRADE_TICK_VALUE) / tick : 0;

   Level("TG_ENTRY", entry, CLR_ENTRY, 2, (isBuy ? "COMPRA " : "VENDA ") + Px(entry));
   if(sl > 0)
     {
      double risk = (sl - entry) * sign * pv * vol;
      Level("TG_STOP", sl, CLR_DOWN, 3, (risk >= 0 ? "STOP (lucro garantido) " : "STOP ") + Px(sl) + "   " + Brl(risk));
     }
   else RemoveLevel("TG_STOP");
   if(tp > 0) Level("TG_TARGET", tp, CLR_UP, 3, "ALVO " + Px(tp) + "   " + Brl((tp - entry) * sign * pv * vol));
   else RemoveLevel("TG_TARGET");

   // Escala: velas visíveis + entrada, stop e alvo, com folga.
   int first = (int)ChartGetInteger(0, CHART_FIRST_VISIBLE_BAR);
   int count = (int)ChartGetInteger(0, CHART_VISIBLE_BARS);
   double highs[], lows[];
   double hi = entry, lo = entry;
   if(count > 0 && CopyHigh(_Symbol, PERIOD_CURRENT, MathMax(0, first - count + 1), count, highs) > 0
      && CopyLow(_Symbol, PERIOD_CURRENT, MathMax(0, first - count + 1), count, lows) > 0)
     {
      hi = MathMax(hi, highs[ArrayMaximum(highs)]);
      lo = MathMin(lo, lows[ArrayMinimum(lows)]);
     }
   if(sl > 0) { hi = MathMax(hi, sl); lo = MathMin(lo, sl); }
   if(tp > 0) { hi = MathMax(hi, tp); lo = MathMin(lo, tp); }
   double pad = MathMax((hi - lo) * 0.08, tick * 4);
   ChartSetInteger(0, CHART_SCALEFIX, true);
   ChartSetDouble(0, CHART_FIXED_MAX, hi + pad);
   ChartSetDouble(0, CHART_FIXED_MIN, lo - pad);
   g_scaleFixed = true;
   ChartRedraw();
  }

//+------------------------------------------------------------------+
//| Troca automática de contrato (WDOV26 -> WDOX26 ...)              |
//+------------------------------------------------------------------+
// Contrato vigente = o de vencimento mais próximo que ainda está a mais de InpRolloverDays dias.
string FrontContract()
  {
   string prefix = StringSubstr(_Symbol, 0, 3);
   datetime limit = TimeTradeServer() + InpRolloverDays * 86400;
   string best = "";
   datetime bestExp = 0;
   int total = SymbolsTotal(false);
   for(int i = 0; i < total; i++)
     {
      string name = SymbolName(i, false);
      if(StringLen(name) != 6 || StringSubstr(name, 0, 3) != prefix) continue;
      datetime exp = (datetime)SymbolInfoInteger(name, SYMBOL_EXPIRATION_TIME);
      if(exp <= limit) continue;
      if(SymbolInfoInteger(name, SYMBOL_TRADE_MODE) == SYMBOL_TRADE_MODE_DISABLED) continue;
      if(best == "" || exp < bestExp) { best = name; bestExp = exp; }
     }
   return best;
  }

// Nunca troca com posição aberta: espera zerar.
bool RolloverIfNeeded()
  {
   g_lastRolloverCheck = GetTickCount64();
   if(PositionSelect(_Symbol)) return false;
   string front = FrontContract();
   if(front == "" || front == _Symbol) return false;
   SymbolSelect(front, true);
   Print("TradeGuard: trocando ", _Symbol, " -> ", front, " (contrato vigente)");
   return ChartSetSymbolPeriod(0, front, Period());
  }

void SendTick()
  {
   MqlTick t;
   if(!SymbolInfoTick(_Symbol, t)) return;
   ulong now = GetTickCount64();
   if(t.bid == g_lastBid && t.ask == g_lastAsk && now - g_lastTickMs < 1000) return;
   if(now - g_lastTickMs < 100) return; // no máx. 10 por segundo
   g_lastBid = t.bid;
   g_lastAsk = t.ask;
   g_lastTickMs = now;
   Send("tick", "\"bid\":" + D(t.bid) + ",\"ask\":" + D(t.ask) + ",\"last\":" + D(t.last)
        + ",\"tms\":" + IntegerToString(t.time_msc));
  }

//+------------------------------------------------------------------+
//| Fluxo: Times & Trades (lado agressor) e book                      |
//+------------------------------------------------------------------+
string Side(uint flags)
  {
   if((flags & TICK_FLAG_BUY) != 0 && (flags & TICK_FLAG_SELL) == 0) return "b";
   if((flags & TICK_FLAG_SELL) != 0 && (flags & TICK_FLAG_BUY) == 0) return "s";
   return "u"; // leilão/negócio direto: sem agressor
  }

double Vol(const MqlTick &t) { return t.volume_real > 0 ? t.volume_real : (double)t.volume; }

// Negócios novos desde o último envio, em lotes: "tms,preço,volume,lado;..."
void SendTrades()
  {
   if(GetTickCount64() - g_lastTradesMs < 200) return;
   g_lastTradesMs = GetTickCount64();
   if(g_tradeMsc == 0)
     {
      MqlTick now;
      if(!SymbolInfoTick(_Symbol, now)) return;
      g_tradeMsc = now.time_msc; // começa do agora (o passado vem pelo histórico)
      g_tradeSameMs = 0;
     }
   MqlTick ticks[];
   int n = CopyTicksRange(_Symbol, ticks, COPY_TICKS_TRADE, (ulong)g_tradeMsc, 0);
   if(n <= 0) return;
   string data = "";
   int count = 0, skip = g_tradeSameMs;
   for(int i = 0; i < n; i++)
     {
      if(ticks[i].time_msc == g_tradeMsc && skip > 0) { skip--; continue; }
      if(ticks[i].time_msc != g_tradeMsc) { g_tradeMsc = ticks[i].time_msc; g_tradeSameMs = 0; }
      g_tradeSameMs++;
      data += (count > 0 ? ";" : "") + IntegerToString(ticks[i].time_msc) + "," + D(ticks[i].last) + ","
              + DoubleToString(Vol(ticks[i]), 0) + "," + Side(ticks[i].flags);
      if(++count >= 300) { Send("trades", "\"d\":\"" + data + "\""); data = ""; count = 0; }
     }
   if(count > 0) Send("trades", "\"d\":\"" + data + "\"");
  }

// Book: 5 melhores níveis de cada lado, no máx. 2x por segundo. "preço:volume,..."
void SendBook()
  {
   if(!g_bookOn || GetTickCount64() - g_lastBookMs < 500) return;
   MqlBookInfo book[];
   if(!MarketBookGet(_Symbol, book)) return;
   g_lastBookMs = GetTickCount64();
   string bids = "", asks = "";
   int nb = 0, na = 0;
   // Vendas vêm do preço maior para o menor; compras logo depois, do maior para o menor.
   for(int i = ArraySize(book) - 1; i >= 0 && na < 5; i--)
      if(book[i].type == BOOK_TYPE_SELL || book[i].type == BOOK_TYPE_SELL_MARKET)
        { asks += (na > 0 ? "," : "") + D(book[i].price) + ":" + DoubleToString(book[i].volume_real > 0 ? book[i].volume_real : (double)book[i].volume, 0); na++; }
   for(int i = 0; i < ArraySize(book) && nb < 5; i++)
      if(book[i].type == BOOK_TYPE_BUY || book[i].type == BOOK_TYPE_BUY_MARKET)
        { bids += (nb > 0 ? "," : "") + D(book[i].price) + ":" + DoubleToString(book[i].volume_real > 0 ? book[i].volume_real : (double)book[i].volume, 0); nb++; }
   Send("book", "\"b\":\"" + bids + "\",\"a\":\"" + asks + "\"");
  }

// Diagnóstico: o que a corretora realmente entrega para análise de fluxo.
void SendFlowCaps()
  {
   MqlTick ticks[];
   ulong nowMs = (ulong)TimeCurrent() * 1000;
   int n = CopyTicksRange(_Symbol, ticks, COPY_TICKS_TRADE, nowMs - 30 * 60 * 1000, nowMs);
   int withSide = 0, withVol = 0;
   for(int i = 0; i < n; i++)
     {
      if(Side(ticks[i].flags) != "u") withSide++;
      if(Vol(ticks[i]) > 0) withVol++;
     }
   MqlBookInfo book[];
   int depth = g_bookOn && MarketBookGet(_Symbol, book) ? ArraySize(book) : -1;
   // Histórico de negócios: 1 hora de 5 pregões atrás (o servidor pode baixar na primeira vez).
   MqlTick old[];
   int hist = CopyTicksRange(_Symbol, old, COPY_TICKS_TRADE, nowMs - 7 * 86400000, nowMs - 7 * 86400000 + 3600000);
   Send("flowcaps", "\"n\":" + IntegerToString(n) + ",\"side\":" + IntegerToString(withSide) + ",\"vol\":" + IntegerToString(withVol)
        + ",\"depth\":" + IntegerToString(depth) + ",\"hist\":" + IntegerToString(hist));
  }

// Exporta 1 pregão por vez para MQL5\Files\TradeGuard\ticks\AAAA-MM-DD.csv (tms;preço;volume;agressor),
// em horário do servidor. Pula fim de semana e dia já exportado. Não trava o gráfico: um dia a cada 2 s.
void ExportTickHistory()
  {
   if(g_histDaysLeft <= 0 || GetTickCount64() - g_lastHistMs < 2000) return;
   g_lastHistMs = GetTickCount64();
   MqlDateTime d;
   TimeToStruct(g_histDay, d);
   datetime start = StringToTime(StringFormat("%04d.%02d.%02d 00:00", d.year, d.mon, d.day));
   string name = StringFormat("TradeGuard\\ticks\\%04d-%02d-%02d.csv", d.year, d.mon, d.day);
   bool weekend = d.day_of_week == 0 || d.day_of_week == 6;
   if(weekend || FileIsExist(name))
     {
      g_histDay -= 86400;
      if(!weekend) g_histDaysLeft--;
      return;
     }
   MqlTick ticks[];
   int n = CopyTicksRange(_Symbol, ticks, COPY_TICKS_TRADE, (ulong)start * 1000, (ulong)(start + 86400) * 1000 - 1);
   if(n < 0 && ++g_histTries < 5) return; // servidor ainda baixando: tenta de novo
   g_histTries = 0;
   if(n > 0)
     {
      int h = FileOpen(name, FILE_WRITE | FILE_TXT | FILE_ANSI);
      if(h != INVALID_HANDLE)
        {
         string chunk = "";
         for(int i = 0; i < n; i++)
           {
            chunk += IntegerToString(ticks[i].time_msc) + ";" + D(ticks[i].last) + ";" + DoubleToString(Vol(ticks[i]), 0) + ";" + Side(ticks[i].flags) + "\n";
            if(i % 2000 == 1999) { FileWriteString(h, chunk); chunk = ""; }
           }
         FileWriteString(h, chunk);
         FileClose(h);
        }
      Send("log", "\"lvl\":\"info\",\"msg\":\"Historico de negocios exportado: " + StringFormat("%02d/%02d", d.day, d.mon) + " (" + IntegerToString(n) + " negocios)\"");
     }
   g_histDay -= 86400;
   g_histDaysLeft--;
  }

// Candles de 5 min da série contínua (ex.: WDO$) dos últimos N dias → MQL5\Files\TradeGuard\bars\SIMBOLO_M5.csv
// (horário do servidor). Base para testar estratégias em anos, não em dias.
void ExportBarHistory(string sym, int days)
  {
   if(sym == "") sym = "WDO$";
   SymbolSelect(sym, true);
   MqlRates rates[];
   datetime to = TimeTradeServer();
   int n = CopyRates(sym, PERIOD_M5, to - (datetime)days * 86400, to, rates);
   if(n <= 0)
     {
      Send("log", "\"lvl\":\"warn\",\"msg\":\"Historico de candles " + Esc(sym) + " ainda nao disponivel (" + IntegerToString(GetLastError()) + "); tente de novo em instantes\"");
      return;
     }
   string name = "TradeGuard\\bars\\" + (sym == "WDO$" ? "WDO_cont" : sym == "WIN$" ? "WIN_cont" : sym) + "_M5.csv";
   int h = FileOpen(name, FILE_WRITE | FILE_TXT | FILE_ANSI);
   if(h == INVALID_HANDLE) return;
   string chunk = "";
   for(int i = 0; i < n; i++)
     {
      chunk += IntegerToString((long)rates[i].time) + ";" + D(rates[i].open) + ";" + D(rates[i].high) + ";" + D(rates[i].low) + ";" + D(rates[i].close)
               + ";" + IntegerToString(rates[i].real_volume > 0 ? rates[i].real_volume : rates[i].tick_volume) + "\n";
      if(i % 2000 == 1999) { FileWriteString(h, chunk); chunk = ""; }
     }
   FileWriteString(h, chunk);
   FileClose(h);
   Send("log", "\"lvl\":\"info\",\"msg\":\"Historico de candles exportado: " + Esc(sym) + " " + IntegerToString(n) + " candles de 5 min\"");
  }

// Notícias de alto impacto (USD e BRL) dos últimos N dias → MQL5\Files\TradeGuard\news\eventos.csv
// (horário do servidor; moeda; nome). Para testar o filtro de notícias no histórico.
void ExportNewsHistory(int days)
  {
   datetime to = TimeTradeServer() + 86400;
   datetime from = to - (datetime)days * 86400;
   int h = FileOpen("TradeGuard\\news\\eventos.csv", FILE_WRITE | FILE_TXT | FILE_ANSI);
   if(h == INVALID_HANDLE) return;
   string cur[] = {"USD", "BRL"};
   int total = 0;
   for(int c = 0; c < ArraySize(cur); c++)
     {
      MqlCalendarValue values[];
      int n = CalendarValueHistory(values, from, to, NULL, cur[c]);
      for(int i = 0; i < n; i++)
        {
         MqlCalendarEvent ev;
         if(!CalendarEventById(values[i].event_id, ev) || ev.importance != CALENDAR_IMPORTANCE_HIGH) continue;
         FileWriteString(h, IntegerToString((long)values[i].time) + ";" + cur[c] + ";" + ev.name + "\n");
         total++;
        }
     }
   FileClose(h);
   Send("log", "\"lvl\":\"info\",\"msg\":\"Historico de noticias exportado: " + IntegerToString(total) + " eventos de alto impacto\"");
  }

// Abre o gráfico de cada ativo extra (ex.: WIN$) com o EA já configurado (modelo TradeGuard<ATIVO>.tpl gravado pelo app).
// Não duplica: se já existe gráfico daquele ativo, não abre outro.
void OpenExtraCharts()
  {
   if(InpOpenCharts == "") return;
   string list[];
   int n = StringSplit(InpOpenCharts, ',', list);
   for(int i = 0; i < n; i++)
     {
      string sym = list[i];
      StringTrimLeft(sym); StringTrimRight(sym);
      if(sym == "") continue;
      string prefix = StringSubstr(sym, 0, 3);
      bool open = false;
      for(long id = ChartFirst(); id >= 0; id = ChartNext(id))
         if(StringSubstr(ChartSymbol(id), 0, 3) == prefix) { open = true; break; }
      if(open) continue;
      SymbolSelect(sym, true);
      long chart = ChartOpen(sym, PERIOD_M5);
      if(chart == 0) { Print("TradeGuard: não consegui abrir o gráfico de ", sym); continue; }
      if(!ChartApplyTemplate(chart, "TradeGuard" + prefix + ".tpl"))
         Print("TradeGuard: modelo TradeGuard", prefix, ".tpl não aplicado (", GetLastError(), ")");
      else
         Print("TradeGuard: gráfico de ", sym, " aberto com o robô.");
     }
  }

void OnBookEvent(const string &symbol)
  {
   if(symbol == _Symbol && g_ready) SendBook();
  }

void SendNews()
  {
   datetime now = TimeTradeServer();
   if(now - g_lastNews < 1800) return; // a cada 30 min
   g_lastNews = now;

   string currencies[] = {"USD", "BRL"};
   for(int c = 0; c < ArraySize(currencies); c++)
     {
      MqlCalendarValue values[];
      if(CalendarValueHistory(values, now - 3600, now + 86400, NULL, currencies[c]) <= 0) continue;
      for(int i = 0; i < ArraySize(values); i++)
        {
         MqlCalendarEvent ev;
         if(!CalendarEventById(values[i].event_id, ev)) continue;
         if(ev.importance < CALENDAR_IMPORTANCE_MODERATE) continue;
         int imp = ev.importance == CALENDAR_IMPORTANCE_HIGH ? 3 : 2;
         Send("news", "\"time\":" + IntegerToString((long)values[i].time) + ",\"cur\":\"" + currencies[c]
              + "\",\"imp\":" + IntegerToString(imp) + ",\"name\":\"" + Esc(ev.name) + "\"");
        }
     }
   Send("newsend", "");
  }

//+------------------------------------------------------------------+
//| Proteções locais (funcionam mesmo sem o app)                     |
//+------------------------------------------------------------------+
bool AppAlive() { return g_ready && GetTickCount64() - g_lastRxMs < (ulong)InpAppTimeoutSec * 1000; }

void LocalSafety()
  {
   if(!PositionSelect(_Symbol)) { g_noStopSince = 0; return; }

   // Zeragem de emergência pelo fuso da B3 calculado no .NET (não congela UTC-3 no código do EA).
   MqlDateTime st;
   TimeToStruct(TimeGMT() + InpB3UtcOffsetMinutes * 60, st);
   string hm = StringFormat("%02d:%02d", st.hour, st.min);
   if(InpHardClose != "" && hm >= InpHardClose)
     {
      if(GetTickCount64() - g_lastHardClose > 5000)
        {
         g_lastHardClose = GetTickCount64();
         DoCloseAll("", "zeragem de emergência " + InpHardClose);
        }
      return;
     }

   // Posição sem stop e app fora → stop de emergência.
   if(PositionGetDouble(POSITION_SL) == 0)
     {
      if(g_noStopSince == 0) g_noStopSince = GetTickCount64();
      if(!AppAlive() && GetTickCount64() - g_noStopSince > 3000)
        {
         double pts = InpFailSafeStopPts;
         double open = PositionGetDouble(POSITION_PRICE_OPEN);
         bool isBuy = PositionGetInteger(POSITION_TYPE) == POSITION_TYPE_BUY;
         double sl = NormPrice(isBuy ? open - pts : open + pts);
         if(g_trade.PositionModify(_Symbol, sl, PositionGetDouble(POSITION_TP)))
            Notify("TradeGuard: app fora do ar - stop de emergência em " + D(sl));
         g_noStopSince = GetTickCount64();
        }
     }
   else g_noStopSince = 0;
  }

//+------------------------------------------------------------------+
//| Eventos                                                          |
//+------------------------------------------------------------------+
int OnInit()
  {
   if(!FromHex(InpSecret, g_master) || ArraySize(g_master) < 16)
     {
      Alert("TradeGuard: cole a chave da tela 'Conexão MT5' em InpSecret.");
      return INIT_PARAMETERS_INCORRECT;
     }
    MathSrand((uint)GetMicrosecondCount());
    g_trade.SetTypeFillingBySymbol(_Symbol);
    double point = SymbolInfoDouble(_Symbol, SYMBOL_POINT);
    double tick = SymbolInfoDouble(_Symbol, SYMBOL_TRADE_TICK_SIZE);
    ulong deviation = point > 0 && tick > 0 ? (ulong)MathCeil(InpDeviationTicks * tick / point) : 10;
    g_trade.SetDeviationInPoints(deviation);
   g_trade.LogLevel(LOG_LEVEL_ERRORS);
   EventSetMillisecondTimer(50);
   g_bookOn = MarketBookAdd(_Symbol);
   ApplyChartStyle();
   g_switching = RolloverIfNeeded();
   if(!g_switching)
     {
      Print("TradeGuard ", EA_VERSION, ": iniciado em ", _Symbol, IsDemo() ? " (DEMO)" : " (REAL)");
      OpenExtraCharts();
     }
   return INIT_SUCCEEDED;
  }

void OnDeinit(const int reason)
  {
   EventKillTimer();
   if(g_bookOn) MarketBookRelease(_Symbol);
   ClearLevels();
   ObjectsDeleteAll(0, "TG_");
   Disconnect("EA removido");
  }

void OnTick()
  {
   if(g_ready) SendTick();
  }

void OnTimer()
  {
   if(g_switching) return;
   if(GetTickCount64() - g_lastRolloverCheck > 3600000 && RolloverIfNeeded())
     {
      g_switching = true;
      Disconnect("troca de contrato");
      return;
     }

   TryConnect();
   ReadIncoming();

   // Canal aberto mas o app não respondeu ao hello (ex.: app reabrindo): fecha e tenta de novo em vez de ficar pendurado.
   if(g_pipe != INVALID_HANDLE && !g_ready && GetTickCount64() - g_lastConnectTry > 5000)
     {
      Print("TradeGuard: app não respondeu ao handshake; tentando de novo.");
      Disconnect("sem resposta ao handshake");
     }

   if(g_ready)
     {
      if(!AppAlive()) Disconnect("app sem responder");
      else
        {
         SendPosition(false);
         if(GetTickCount64() - g_lastAcctMs > 1000) SendAccount();
         SendNews();
        }
     }
   if(GetTickCount64() - g_lastLevelsMs > 300) DrawLevels();
   if(GetTickCount64() - g_lastStudiesMs > 2000) DrawStudies();
   if(g_ready) SendBars(false);
   if(g_ready) SendTrades();
   if(g_ready) ExportTickHistory();
   LocalSafety();
  }

// Campos de um negócio (o ticket precisa estar no histórico selecionado).
string DealFields(const ulong ticket)
  {
   long entry = HistoryDealGetInteger(ticket, DEAL_ENTRY);
   string e = entry == DEAL_ENTRY_IN ? "in" : entry == DEAL_ENTRY_OUT ? "out" : entry == DEAL_ENTRY_INOUT ? "inout" : "other";
   string side = HistoryDealGetInteger(ticket, DEAL_TYPE) == DEAL_TYPE_BUY ? "buy" : "sell";
   double profit = HistoryDealGetDouble(ticket, DEAL_PROFIT)
                   + HistoryDealGetDouble(ticket, DEAL_COMMISSION)
                   + HistoryDealGetDouble(ticket, DEAL_FEE);
   string sym = HistoryDealGetString(ticket, DEAL_SYMBOL);
   return "\"ticket\":" + IntegerToString((long)ticket)
          + ",\"pid\":" + IntegerToString(HistoryDealGetInteger(ticket, DEAL_POSITION_ID))
          + ",\"sym\":\"" + Esc(sym) + "\""
          + ",\"time\":" + IntegerToString((long)HistoryDealGetInteger(ticket, DEAL_TIME))
          + ",\"side\":\"" + side + "\""
          + ",\"vol\":" + DoubleToString(HistoryDealGetDouble(ticket, DEAL_VOLUME), 0)
          + ",\"price\":" + DoubleToString(HistoryDealGetDouble(ticket, DEAL_PRICE), (int)SymbolInfoInteger(sym, SYMBOL_DIGITS))
          + ",\"profit\":" + DoubleToString(profit, 2)
          + ",\"entry\":\"" + e + "\""
          + ",\"comment\":\"" + Esc(HistoryDealGetString(ticket, DEAL_COMMENT)) + "\"";
  }

// Histórico de negócios do mini dólar (todos os vencimentos) dos últimos InpHistoryDays dias:
// o app remonta os trades feitos com ele fechado e completa o diário.
void SendHistory()
  {
   datetime now = TimeTradeServer();
   if(!HistorySelect(now - InpHistoryDays * 86400, now + 3600)) return;
   string prefix = StringSubstr(_Symbol, 0, 3);
   int total = HistoryDealsTotal();
   for(int i = 0; i < total; i++)
     {
      ulong ticket = HistoryDealGetTicket(i);
      if(ticket == 0) continue;
      if(StringSubstr(HistoryDealGetString(ticket, DEAL_SYMBOL), 0, 3) != prefix) continue;
      long type = HistoryDealGetInteger(ticket, DEAL_TYPE);
      if(type != DEAL_TYPE_BUY && type != DEAL_TYPE_SELL) continue;
      Send("hdeal", DealFields(ticket));
     }
   Send("hend", "");
  }

void OnTradeTransaction(const MqlTradeTransaction &trans, const MqlTradeRequest &request, const MqlTradeResult &result)
  {
   if(trans.type != TRADE_TRANSACTION_DEAL_ADD || trans.symbol != _Symbol) return;
   if(!HistoryDealSelect(trans.deal)) return;

   // Negócio primeiro, posição depois: o app fecha o trade com o resultado já em mãos.
   Send("deal", DealFields(trans.deal));
   SendPosition(true);
  }
//+------------------------------------------------------------------+
