import { useEffect, useRef, useState } from "react";
import * as signalR from "@microsoft/signalr";
import { CartesianGrid, Line, LineChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from "recharts";
import { fetchMatches, fetchMatchMarkets, fetchOddsHistory, Match, MarketRow, OddsPoint, REALTIME_URL } from "./api";
import { ClockState, display, isPausedPhase, resync } from "./clock";

type Conn = "connecting" | "connected" | "disconnected";

const C = {
  bg: "#f4f5fb", card: "#ffffff", border: "#e6e6f0", text: "#1c1c2e",
  muted: "#8a8aa0", live: "#e23b3b", accent: "#3b4ce2", pill: "#eef0fb",
};

const teamName = (zh: string | null, en: string) => zh ?? en;
const fmtOdds = (v: number | null | undefined) => (v == null ? "—" : v.toFixed(2));
const hhmmss = (iso: string) => new Date(iso.endsWith("Z") ? iso : iso + "Z").toLocaleTimeString("zh-TW", { hour12: false });
const fmtKickoff = (iso: string) =>
  new Date(iso.endsWith("Z") ? iso : iso + "Z").toLocaleString("zh-TW", { month: "numeric", day: "numeric", hour: "2-digit", minute: "2-digit", hour12: false });

function statusLabel(m: Match, clockText: string | null): string {
  if (m.status === "Live") return `${m.livePhase ?? ""} ${clockText ?? m.matchMinute ?? "進行中"}`.trim();
  if (m.status === "Ended") return "已結束";
  return "未開賽";
}

interface SelRef { id: string; name: string; }

export function App() {
  const [matches, setMatches] = useState<Record<string, Match>>({});
  const [conn, setConn] = useState<Conn>("connecting");
  const [odds, setOdds] = useState<Record<string, number>>({});
  const [clocks, setClocks] = useState<Record<string, ClockState>>({});
  const [nowMs, setNowMs] = useState<number>(Date.now());
  const [expandedId, setExpandedId] = useState<string | null>(null);
  const [markets, setMarkets] = useState<MarketRow[]>([]);
  const [sel, setSel] = useState<SelRef | null>(null);
  const [history, setHistory] = useState<OddsPoint[]>([]);
  const selRef = useRef<string | null>(null);
  selRef.current = sel?.id ?? null;

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
      .catch(console.error);

    const hub = new signalR.HubConnectionBuilder().withUrl(`${REALTIME_URL}/hubs/live`).withAutomaticReconnect().build();

    hub.on("ScoreUpdated", (p: { matchExternalId: string; home: number; away: number; minute: string | null }) =>
      setMatches((prev) => { const m = prev[p.matchExternalId]; return m ? { ...prev, [p.matchExternalId]: { ...m, homeScore: p.home, awayScore: p.away, matchMinute: p.minute } } : prev; }));

    hub.on("StatusUpdated", (p: { matchExternalId: string; status: string; livePhase: string | null }) =>
      setMatches((prev) => { const m = prev[p.matchExternalId]; return m ? { ...prev, [p.matchExternalId]: { ...m, status: p.status, livePhase: p.livePhase } } : prev; }));

    hub.on("OddsUpdated", (p: { matchExternalId: string; selectionExternalId: string; newOdds: number }) => {
      setOdds((prev) => ({ ...prev, [p.selectionExternalId]: p.newOdds }));
      if (p.selectionExternalId === selRef.current)
        setHistory((prev) => [...prev, { decimalOdds: p.newOdds, fetchedUtc: new Date().toISOString() }]);
    });

    hub.on("ClockUpdated", (p: { matchExternalId: string; matchMinute: string | null; livePhase: string | null; running: boolean }) =>
      setClocks((prev) => { const next = resync(prev[p.matchExternalId], p.matchMinute, p.livePhase, p.running, Date.now()); return next ? { ...prev, [p.matchExternalId]: next } : prev; }));

    hub.onreconnecting(() => setConn("connecting"));
    hub.onreconnected(() => setConn("connected"));
    hub.onclose(() => setConn("disconnected"));
    hub.start().then(() => mounted && setConn("connected")).catch(() => setConn("disconnected"));

    return () => { mounted = false; hub.stop(); };
  }, []);

  const toggle = async (id: string) => {
    if (expandedId === id) { setExpandedId(null); setMarkets([]); return; }
    setExpandedId(id);
    setMarkets([]);
    try {
      const rows = await fetchMatchMarkets(id);
      setMarkets(rows);
      seedOdds(rows.map((r) => [r.selectionExternalId, r.decimalOdds]));
    } catch (e) { console.error(e); }
  };

  const openChart = async (id: string, name: string) => {
    setSel({ id, name });
    setHistory([]);
    try { setHistory(await fetchOddsHistory(id)); } catch (e) { console.error(e); }
  };
  const closeChart = () => { setSel(null); setHistory([]); };

  const rows = Object.values(matches).sort((a, b) => a.kickoffUtc.localeCompare(b.kickoffUtc));

  return (
    <div style={{ minHeight: "100vh", background: C.bg, color: C.text, fontFamily: "system-ui, -apple-system, 'Noto Sans TC', sans-serif" }}>
      <div style={{ maxWidth: 900, margin: "0 auto", padding: "28px 16px 60px" }}>
        <header style={{ display: "flex", alignItems: "center", justifyContent: "space-between", marginBottom: 6 }}>
          <h1 style={{ fontSize: 25, margin: 0, letterSpacing: -0.5 }}>⚽ WorldCup Live Hub</h1>
          <span style={{ fontSize: 12, fontWeight: 600, padding: "4px 10px", borderRadius: 999, background: conn === "connected" ? "#e6f7ef" : "#fdeaea", color: conn === "connected" ? "#0a8a55" : C.live }}>
            {conn === "connected" ? "● 即時連線" : conn === "connecting" ? "○ 連線中…" : "× 已斷線"}
          </span>
        </header>
        <p style={{ color: C.muted, fontSize: 13, margin: "0 0 20px" }}>2026 世界盃 · 比分 / 時間 / 賠率即時更新 · 點賠率看走勢</p>

        <div style={{ display: "grid", gap: 10 }}>
          {rows.map((m) => {
            const live = m.status === "Live";
            const open = expandedId === m.externalId;
            return (
              <div key={m.externalId} style={{ borderRadius: 14, border: `1px solid ${C.border}`, background: C.card, boxShadow: "0 1px 3px rgba(20,20,50,.04)", overflow: "hidden" }}>
                <div onClick={() => toggle(m.externalId)} style={{ display: "grid", gridTemplateColumns: "1fr 120px 1fr 168px", alignItems: "center", gap: 10, padding: "14px 18px", cursor: "pointer" }}>
                  <div style={{ textAlign: "right", fontWeight: 600, fontSize: 15 }}>{teamName(m.homeTeamZh, m.homeTeamEn)}</div>
                  <div style={{ textAlign: "center" }}>
                    {m.status === "Scheduled" ? (
                      <>
                        <div style={{ fontSize: 16, fontWeight: 700, fontVariantNumeric: "tabular-nums", lineHeight: 1.2 }}>{fmtKickoff(m.kickoffUtc)}</div>
                        <div style={{ fontSize: 11, marginTop: 3, display: "inline-block", padding: "1px 8px", borderRadius: 999, fontWeight: 600, background: "#f0f0f6", color: C.muted }}>未開賽</div>
                      </>
                    ) : (
                      <>
                        <div style={{ fontSize: 24, fontWeight: 800, fontVariantNumeric: "tabular-nums", lineHeight: 1.1 }}>{m.homeScore}<span style={{ color: C.muted, margin: "0 6px" }}>:</span>{m.awayScore}</div>
                        <div style={{ fontSize: 11, marginTop: 3, display: "inline-block", padding: "1px 8px", borderRadius: 999, fontWeight: 600, background: live ? "#fdeaea" : "#f0f0f6", color: live ? C.live : C.muted, fontVariantNumeric: "tabular-nums" }}>
                          {live && <span style={{ display: "inline-block", width: 6, height: 6, borderRadius: 999, background: C.live, marginRight: 5, verticalAlign: "middle" }} />}
                          {statusLabel(m, display(clocks[m.externalId], nowMs))}
                        </div>
                      </>
                    )}
                  </div>
                  <div style={{ textAlign: "left", fontWeight: 600, fontSize: 15 }}>{teamName(m.awayTeamZh, m.awayTeamEn)}</div>
                  <div style={{ display: "flex", gap: 5, justifyContent: "flex-end" }}>
                    <OddsPill label="主" v={m.homeSelId ? odds[m.homeSelId] : null} active={sel?.id === m.homeSelId} onClick={() => m.homeSelId && openChart(m.homeSelId, `不讓分 · ${teamName(m.homeTeamZh, m.homeTeamEn)}`)} />
                    <OddsPill label="和" v={m.drawSelId ? odds[m.drawSelId] : null} active={sel?.id === m.drawSelId} onClick={() => m.drawSelId && openChart(m.drawSelId, "不讓分 · 和局")} />
                    <OddsPill label="客" v={m.awaySelId ? odds[m.awaySelId] : null} active={sel?.id === m.awaySelId} onClick={() => m.awaySelId && openChart(m.awaySelId, `不讓分 · ${teamName(m.awayTeamZh, m.awayTeamEn)}`)} />
                  </div>
                </div>

                {open && (
                  <div style={{ borderTop: `1px solid ${C.border}`, padding: "14px 18px", background: "#fbfbfe" }}>
                    {markets.length === 0
                      ? <div style={{ color: C.muted, fontSize: 13 }}>載入盤口中…</div>
                      : <MarketSections groups={groupMarkets(markets)} odds={odds} selId={sel?.id ?? null} onPick={openChart} />}
                  </div>
                )}
              </div>
            );
          })}
          {rows.length === 0 && <p style={{ color: C.muted }}>尚無賽事資料…</p>}
        </div>
      </div>

      {sel && <ChartModal sel={sel} history={history} current={odds[sel.id]} onClose={closeChart} />}
    </div>
  );
}

function OddsPill({ label, v, active, onClick }: { label: string; v: number | null | undefined; active?: boolean; onClick?: () => void }) {
  const clickable = v != null && !!onClick;
  return (
    <span onClick={clickable ? (e) => { e.stopPropagation(); onClick!(); } : undefined} title={clickable ? "看賠率走勢" : undefined}
      style={{ display: "inline-flex", flexDirection: "column", alignItems: "center", minWidth: 46, padding: "3px 6px", borderRadius: 8, cursor: clickable ? "pointer" : "default", background: active ? C.text : C.pill, color: active ? "#fff" : C.text, transition: "background .15s" }}>
      <span style={{ fontSize: 10, color: active ? "#b9bce8" : C.muted }}>{label}</span>
      <b style={{ fontSize: 13, fontVariantNumeric: "tabular-nums" }}>{v == null ? "—" : v.toFixed(2)}</b>
    </span>
  );
}

function MarketSections({ groups, odds, selId, onPick }: { groups: MarketGroup[]; odds: Record<string, number>; selId: string | null; onPick: (id: string, name: string) => void }) {
  const liveG = groups.filter((g) => g.source === "Live");
  const preG = groups.filter((g) => g.source !== "Live");
  return (
    <div style={{ display: "grid", gap: 14 }}>
      {liveG.length > 0 && <Section title="即時盤" accent={C.live} groups={liveG} odds={odds} selId={selId} onPick={onPick} />}
      {preG.length > 0 && <Section title="賽前盤" accent={C.muted} groups={preG} odds={odds} selId={selId} onPick={onPick} />}
    </div>
  );
}

function Section({ title, accent, groups, odds, selId, onPick }: { title: string; accent: string; groups: MarketGroup[]; odds: Record<string, number>; selId: string | null; onPick: (id: string, name: string) => void }) {
  return (
    <div>
      <div style={{ fontSize: 12, fontWeight: 700, color: accent, marginBottom: 8, display: "flex", alignItems: "center", gap: 6 }}>
        <span style={{ width: 7, height: 7, borderRadius: 999, background: accent }} />{title}
        <span style={{ color: C.muted, fontWeight: 400 }}>· {groups.length} 種玩法</span>
      </div>
      <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(240px, 1fr))", gap: 8 }}>
        {groups.map((g) => (
          <div key={g.marketExternalId} style={{ border: `1px solid ${C.border}`, borderRadius: 10, background: C.card, padding: "8px 10px" }}>
            <div style={{ fontSize: 12, fontWeight: 600, marginBottom: 6, color: C.text }}>{g.marketNameZh ?? g.marketNameEn}</div>
            <div style={{ display: "flex", flexWrap: "wrap", gap: 5 }}>
              {g.selections.map((s) => {
                const active = selId === s.selectionExternalId;
                const name = s.selectionNameZh ?? s.selectionNameEn;
                return (
                  <span key={s.selectionExternalId} onClick={() => onPick(s.selectionExternalId, `${g.marketNameZh ?? g.marketNameEn} · ${name}`)} title="看賠率走勢"
                    style={{ fontSize: 12, padding: "3px 9px", borderRadius: 7, cursor: "pointer", whiteSpace: "nowrap", background: active ? C.text : C.pill, color: active ? "#fff" : C.text }}>
                    {name} <b style={{ fontVariantNumeric: "tabular-nums" }}>{fmtOdds(odds[s.selectionExternalId])}</b>
                  </span>
                );
              })}
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}

function ChartModal({ sel, history, current, onClose }: { sel: SelRef; history: OddsPoint[]; current: number | undefined; onClose: () => void }) {
  const data = history.map((p) => ({ t: hhmmss(p.fetchedUtc), odds: p.decimalOdds }));
  return (
    <div onClick={onClose} style={{ position: "fixed", inset: 0, background: "rgba(20,20,50,.45)", display: "flex", alignItems: "center", justifyContent: "center", padding: 16, zIndex: 50 }}>
      <div onClick={(e) => e.stopPropagation()} style={{ width: "min(620px, 100%)", background: "#fff", borderRadius: 16, padding: "18px 20px", boxShadow: "0 12px 40px rgba(20,20,50,.25)" }}>
        <div style={{ display: "flex", alignItems: "flex-start", justifyContent: "space-between", marginBottom: 12 }}>
          <div>
            <div style={{ fontSize: 12, color: C.muted }}>📈 賠率走勢</div>
            <div style={{ fontSize: 16, fontWeight: 700 }}>{sel.name}</div>
            <div style={{ fontSize: 13, color: C.muted, marginTop: 2 }}>
              目前 <b style={{ color: C.text, fontVariantNumeric: "tabular-nums" }}>{fmtOdds(current)}</b> · {history.length} 個資料點
            </div>
          </div>
          <button onClick={onClose} style={{ border: "none", background: C.pill, borderRadius: 8, width: 30, height: 30, fontSize: 16, cursor: "pointer", color: C.text }}>✕</button>
        </div>
        {data.length < 2 ? (
          <div style={{ fontSize: 13, color: C.muted, padding: "30px 0", textAlign: "center" }}>
            資料點不足以繪圖<br /><span style={{ fontSize: 12 }}>（賽前盤開賽後停盤、或此選項尚無賠率變動）</span>
          </div>
        ) : (
          <ResponsiveContainer width="100%" height={260}>
            <LineChart data={data} margin={{ top: 6, right: 16, bottom: 0, left: -12 }}>
              <CartesianGrid strokeDasharray="3 3" stroke="#eee" />
              <XAxis dataKey="t" tick={{ fontSize: 10 }} minTickGap={32} />
              <YAxis domain={["auto", "auto"]} tick={{ fontSize: 10 }} width={42} />
              <Tooltip />
              <Line type="stepAfter" dataKey="odds" stroke={C.live} dot={false} strokeWidth={2} isAnimationActive={false} />
            </LineChart>
          </ResponsiveContainer>
        )}
      </div>
    </div>
  );
}

interface MarketGroup {
  marketExternalId: string; marketNameEn: string; marketNameZh: string | null; source: string; selections: MarketRow[];
}
function groupMarkets(rows: MarketRow[]): MarketGroup[] {
  const map = new Map<string, MarketGroup>();
  for (const r of rows) {
    let g = map.get(r.marketExternalId);
    if (!g) { g = { marketExternalId: r.marketExternalId, marketNameEn: r.marketNameEn, marketNameZh: r.marketNameZh, source: r.source, selections: [] }; map.set(r.marketExternalId, g); }
    g.selections.push(r);
  }
  return [...map.values()];
}
