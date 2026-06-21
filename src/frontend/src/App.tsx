import { useEffect, useRef, useState } from "react";
import * as signalR from "@microsoft/signalr";
import { CartesianGrid, Line, LineChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from "recharts";
import { fetchMatches, fetchMatchMarkets, fetchOddsHistory, Match, MarketRow, OddsPoint, REALTIME_URL } from "./api";
import { ClockState, display, isPausedPhase, resync } from "./clock";

const hhmmss = (iso: string) => {
  const d = new Date(iso.endsWith("Z") ? iso : iso + "Z");
  return d.toLocaleTimeString("zh-TW", { hour12: false });
};

type Conn = "connecting" | "connected" | "disconnected";

const teamName = (zh: string | null, en: string) => zh ?? en;
const fmtOdds = (v: number | null | undefined) => (v == null ? "—" : v.toFixed(2));

function statusLabel(m: Match, clockText: string | null): string {
  if (m.status === "Live") {
    const t = clockText ?? m.matchMinute ?? "進行中";
    return `🔴 ${m.livePhase ?? ""} ${t}`.trim();
  }
  if (m.status === "Ended") return "✓ 已結束";
  return "○ 未開賽";
}

export function App() {
  const [matches, setMatches] = useState<Record<string, Match>>({});
  const [conn, setConn] = useState<Conn>("connecting");
  const [odds, setOdds] = useState<Record<string, number>>({});
  const [clocks, setClocks] = useState<Record<string, ClockState>>({});
  const [nowMs, setNowMs] = useState<number>(Date.now());
  const [expandedId, setExpandedId] = useState<string | null>(null);
  const [markets, setMarkets] = useState<MarketRow[]>([]);
  const [selectedSel, setSelectedSel] = useState<{ id: string; name: string } | null>(null);
  const [history, setHistory] = useState<OddsPoint[]>([]);
  const selectedSelRef = useRef<string | null>(null);
  selectedSelRef.current = selectedSel?.id ?? null;

  // 每秒重繪，讓走鐘平滑前進
  useEffect(() => {
    const id = setInterval(() => setNowMs(Date.now()), 1000);
    return () => clearInterval(id);
  }, []);

  const seedOdds = (entries: [string | null, number | null][]) =>
    setOdds((prev) => {
      const next = { ...prev };
      for (const [id, v] of entries) if (id && v != null && next[id] == null) next[id] = v;
      return next;
    });

  useEffect(() => {
    let mounted = true;
    fetchMatches()
      .then((list) => {
        if (!mounted) return;
        const map: Record<string, Match> = {};
        const seed: [string | null, number | null][] = [];
        const ck: Record<string, ClockState> = {};
        const t0 = Date.now();
        for (const m of list) {
          map[m.externalId] = m;
          seed.push([m.homeSelId, m.homeOdds], [m.drawSelId, m.drawOdds], [m.awaySelId, m.awayOdds]);
          if (m.status === "Live") {
            const c = resync(undefined, m.matchMinute, m.livePhase, !isPausedPhase(m.livePhase), t0);
            if (c) ck[m.externalId] = c;
          }
        }
        setMatches(map);
        setClocks(ck);
        seedOdds(seed);
      })
      .catch((e) => console.error(e));

    const hub = new signalR.HubConnectionBuilder()
      .withUrl(`${REALTIME_URL}/hubs/live`)
      .withAutomaticReconnect()
      .build();

    hub.on("ScoreUpdated", (p: { matchExternalId: string; home: number; away: number; minute: string | null }) =>
      setMatches((prev) => {
        const m = prev[p.matchExternalId];
        return m ? { ...prev, [p.matchExternalId]: { ...m, homeScore: p.home, awayScore: p.away, matchMinute: p.minute } } : prev;
      })
    );

    hub.on("StatusUpdated", (p: { matchExternalId: string; status: string; livePhase: string | null }) =>
      setMatches((prev) => {
        const m = prev[p.matchExternalId];
        return m ? { ...prev, [p.matchExternalId]: { ...m, status: p.status, livePhase: p.livePhase } } : prev;
      })
    );

    hub.on("OddsUpdated", (p: { matchExternalId: string; selectionExternalId: string; newOdds: number }) => {
      setOdds((prev) => ({ ...prev, [p.selectionExternalId]: p.newOdds }));
      if (p.selectionExternalId === selectedSelRef.current)
        setHistory((prev) => [...prev, { decimalOdds: p.newOdds, fetchedUtc: new Date().toISOString() }]);
    });

    hub.on("ClockUpdated", (p: { matchExternalId: string; matchMinute: string | null; livePhase: string | null; running: boolean }) =>
      setClocks((prev) => {
        const next = resync(prev[p.matchExternalId], p.matchMinute, p.livePhase, p.running, Date.now());
        return next ? { ...prev, [p.matchExternalId]: next } : prev;
      })
    );

    hub.onreconnecting(() => setConn("connecting"));
    hub.onreconnected(() => setConn("connected"));
    hub.onclose(() => setConn("disconnected"));
    hub.start().then(() => mounted && setConn("connected")).catch(() => setConn("disconnected"));

    return () => {
      mounted = false;
      hub.stop();
    };
  }, []);

  const loadMarkets = async (id: string) => {
    setMarkets([]);
    try {
      const rows = await fetchMatchMarkets(id);
      setMarkets(rows);
      seedOdds(rows.map((r) => [r.selectionExternalId, r.decimalOdds]));
    } catch (e) {
      console.error(e);
    }
  };

  const toggle = async (id: string) => {
    setSelectedSel(null);
    setHistory([]);
    if (expandedId === id) {
      setExpandedId(null);
      setMarkets([]);
      return;
    }
    setExpandedId(id);
    await loadMarkets(id);
  };

  // 從看板的 1X2 pill 直接點：展開該場並畫該選項走勢
  const openInline = async (matchId: string, selId: string | null, name: string) => {
    if (!selId) return;
    if (expandedId !== matchId) {
      setExpandedId(matchId);
      await loadMarkets(matchId);
    }
    openChart(selId, name);
  };

  const openChart = async (id: string, name: string) => {
    setSelectedSel({ id, name });
    setHistory([]);
    try {
      setHistory(await fetchOddsHistory(id));
    } catch (e) {
      console.error(e);
    }
  };

  const rows = Object.values(matches).sort((a, b) => a.kickoffUtc.localeCompare(b.kickoffUtc));

  return (
    <div style={{ fontFamily: "system-ui, sans-serif", maxWidth: 860, margin: "32px auto", padding: "0 16px", color: "#1a1a2e" }}>
      <header style={{ display: "flex", alignItems: "baseline", justifyContent: "space-between" }}>
        <h1 style={{ fontSize: 24, margin: 0 }}>⚽ WorldCup Live Hub</h1>
        <span style={{ fontSize: 13, color: conn === "connected" ? "#0a7" : "#c33" }}>
          {conn === "connected" ? "● 即時連線中" : conn === "connecting" ? "○ 連線中…" : "× 已斷線"}
        </span>
      </header>
      <p style={{ color: "#666", fontSize: 13 }}>2026 世界盃 · 比分/狀態/賠率即時更新（SignalR）· 點比賽看完整盤口</p>

      <div style={{ display: "grid", gap: 8 }}>
        {rows.map((m) => {
          const live = m.status === "Live";
          const open = expandedId === m.externalId;
          return (
            <div key={m.externalId} style={{ borderRadius: 10, border: "1px solid #e5e5ef", background: live ? "#fff7f7" : "#fafafe", overflow: "hidden" }}>
              <div
                onClick={() => toggle(m.externalId)}
                style={{ display: "grid", gridTemplateColumns: "1fr auto 1fr 150px", alignItems: "center", gap: 12, padding: "12px 16px", cursor: "pointer" }}
              >
                <div style={{ textAlign: "right", fontWeight: 600 }}>{teamName(m.homeTeamZh, m.homeTeamEn)}</div>
                <div style={{ textAlign: "center", minWidth: 92 }}>
                  <div style={{ fontSize: 22, fontWeight: 700, fontVariantNumeric: "tabular-nums" }}>{m.homeScore} : {m.awayScore}</div>
                  <div style={{ fontSize: 11, color: live ? "#c33" : "#888" }}>{statusLabel(m, display(clocks[m.externalId], nowMs))}</div>
                </div>
                <div style={{ textAlign: "left", fontWeight: 600 }}>{teamName(m.awayTeamZh, m.awayTeamEn)}</div>
                <div style={{ display: "flex", gap: 4, justifyContent: "flex-end" }}>
                  <OddsPill label="主" v={m.homeSelId ? odds[m.homeSelId] : null} selected={selectedSel?.id === m.homeSelId}
                    onClick={() => openInline(m.externalId, m.homeSelId, `不讓分 · ${teamName(m.homeTeamZh, m.homeTeamEn)}`)} />
                  <OddsPill label="和" v={m.drawSelId ? odds[m.drawSelId] : null} selected={selectedSel?.id === m.drawSelId}
                    onClick={() => openInline(m.externalId, m.drawSelId, "不讓分 · 和局")} />
                  <OddsPill label="客" v={m.awaySelId ? odds[m.awaySelId] : null} selected={selectedSel?.id === m.awaySelId}
                    onClick={() => openInline(m.externalId, m.awaySelId, `不讓分 · ${teamName(m.awayTeamZh, m.awayTeamEn)}`)} />
                </div>
              </div>

              {open && (
                <div style={{ borderTop: "1px solid #eee", padding: "10px 16px", background: "#fff" }}>
                  {markets.length === 0 ? (
                    <div style={{ color: "#999", fontSize: 13 }}>載入盤口中…</div>
                  ) : (
                    groupMarkets(markets).map((g) => (
                      <div key={g.marketExternalId} style={{ marginBottom: 10 }}>
                        <div style={{ fontSize: 13, fontWeight: 600, marginBottom: 4 }}>
                          {g.marketNameZh ?? g.marketNameEn}
                          <SourceTag source={g.source} />
                        </div>
                        <div style={{ display: "flex", flexWrap: "wrap", gap: 6 }}>
                          {g.selections.map((s) => {
                            const sel = selectedSel?.id === s.selectionExternalId;
                            const name = s.selectionNameZh ?? s.selectionNameEn;
                            return (
                              <span
                                key={s.selectionExternalId}
                                onClick={() => openChart(s.selectionExternalId, `${g.marketNameZh ?? g.marketNameEn} · ${name}`)}
                                title="點看賠率走勢"
                                style={{ fontSize: 12, padding: "3px 8px", borderRadius: 6, cursor: "pointer", whiteSpace: "nowrap", background: sel ? "#1a1a2e" : "#f1f1f8", color: sel ? "#fff" : "inherit" }}
                              >
                                {name} <b style={{ fontVariantNumeric: "tabular-nums" }}>{fmtOdds(odds[s.selectionExternalId])}</b>
                              </span>
                            );
                          })}
                        </div>
                      </div>
                    ))
                  )}

                  {selectedSel && (
                    <div style={{ marginTop: 8, borderTop: "1px dashed #ddd", paddingTop: 8 }}>
                      <div style={{ fontSize: 12, fontWeight: 600, marginBottom: 4 }}>
                        📈 賠率走勢：{selectedSel.name}
                        <span style={{ color: "#999", fontWeight: 400 }}>（{history.length} 點）</span>
                      </div>
                      {history.length < 2 ? (
                        <div style={{ fontSize: 12, color: "#999" }}>資料點不足，等賠率變動或更多歷史…</div>
                      ) : (
                        <ResponsiveContainer width="100%" height={180}>
                          <LineChart data={history.map((p) => ({ t: hhmmss(p.fetchedUtc), odds: p.decimalOdds }))} margin={{ top: 5, right: 12, bottom: 0, left: -16 }}>
                            <CartesianGrid strokeDasharray="3 3" stroke="#eee" />
                            <XAxis dataKey="t" tick={{ fontSize: 10 }} minTickGap={28} />
                            <YAxis domain={["auto", "auto"]} tick={{ fontSize: 10 }} />
                            <Tooltip />
                            <Line type="stepAfter" dataKey="odds" stroke="#c33" dot={false} strokeWidth={2} isAnimationActive={false} />
                          </LineChart>
                        </ResponsiveContainer>
                      )}
                    </div>
                  )}
                </div>
              )}
            </div>
          );
        })}
        {rows.length === 0 && <p style={{ color: "#999" }}>尚無賽事資料…</p>}
      </div>
    </div>
  );
}

function OddsPill({ label, v, selected, onClick }: { label: string; v: number | null | undefined; selected?: boolean; onClick?: () => void }) {
  const clickable = v != null && onClick;
  return (
    <span
      onClick={clickable ? (e) => { e.stopPropagation(); onClick!(); } : undefined}
      title={clickable ? "點看賠率走勢" : undefined}
      style={{
        fontSize: 11, padding: "2px 6px", borderRadius: 6, minWidth: 38, textAlign: "center",
        cursor: clickable ? "pointer" : "default",
        background: selected ? "#1a1a2e" : "#eef",
        color: selected ? "#fff" : "inherit",
      }}
    >
      <span style={{ color: selected ? "#aab" : "#88a" }}>{label}</span>{" "}
      <b style={{ fontVariantNumeric: "tabular-nums" }}>{v == null ? "—" : v.toFixed(2)}</b>
    </span>
  );
}

function SourceTag({ source }: { source: string }) {
  const live = source === "Live";
  return (
    <span style={{ marginLeft: 6, fontSize: 10, padding: "1px 5px", borderRadius: 4, color: "#fff", background: live ? "#c33" : "#779" }}>
      {live ? "即時" : "賽前"}
    </span>
  );
}

interface MarketGroup {
  marketExternalId: string;
  marketNameEn: string;
  marketNameZh: string | null;
  source: string;
  selections: MarketRow[];
}

function groupMarkets(rows: MarketRow[]): MarketGroup[] {
  const map = new Map<string, MarketGroup>();
  for (const r of rows) {
    let g = map.get(r.marketExternalId);
    if (!g) {
      g = { marketExternalId: r.marketExternalId, marketNameEn: r.marketNameEn, marketNameZh: r.marketNameZh, source: r.source, selections: [] };
      map.set(r.marketExternalId, g);
    }
    g.selections.push(r);
  }
  return [...map.values()];
}
