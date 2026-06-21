import { useEffect, useState } from "react";
import * as signalR from "@microsoft/signalr";
import { fetchMatches, Match, REALTIME_URL } from "./api";

type Conn = "connecting" | "connected" | "disconnected";

const teamName = (zh: string | null, en: string) => zh ?? en;

function statusLabel(m: Match): string {
  if (m.status === "Live") return `🔴 進行中${m.livePhase ? ` ${m.livePhase}` : ""}${m.matchMinute ? ` ${m.matchMinute}` : ""}`;
  if (m.status === "Ended") return "✓ 已結束";
  return "○ 未開賽";
}

export function App() {
  const [matches, setMatches] = useState<Record<string, Match>>({});
  const [conn, setConn] = useState<Conn>("connecting");

  useEffect(() => {
    let mounted = true;
    fetchMatches()
      .then((list) => {
        if (!mounted) return;
        const map: Record<string, Match> = {};
        for (const m of list) map[m.externalId] = m;
        setMatches(map);
      })
      .catch((e) => console.error(e));

    const hub = new signalR.HubConnectionBuilder()
      .withUrl(`${REALTIME_URL}/hubs/live`)
      .withAutomaticReconnect()
      .build();

    hub.on("ScoreUpdated", (p: { matchExternalId: string; home: number; away: number; minute: string | null }) => {
      setMatches((prev) => {
        const m = prev[p.matchExternalId];
        if (!m) return prev;
        return { ...prev, [p.matchExternalId]: { ...m, homeScore: p.home, awayScore: p.away, matchMinute: p.minute } };
      });
    });

    hub.on("StatusUpdated", (p: { matchExternalId: string; status: string; livePhase: string | null }) => {
      setMatches((prev) => {
        const m = prev[p.matchExternalId];
        if (!m) return prev;
        return { ...prev, [p.matchExternalId]: { ...m, status: p.status, livePhase: p.livePhase } };
      });
    });

    hub.onreconnecting(() => setConn("connecting"));
    hub.onreconnected(() => setConn("connected"));
    hub.onclose(() => setConn("disconnected"));

    setConn("connecting");
    hub.start().then(() => mounted && setConn("connected")).catch(() => setConn("disconnected"));

    return () => {
      mounted = false;
      hub.stop();
    };
  }, []);

  const rows = Object.values(matches).sort((a, b) => a.kickoffUtc.localeCompare(b.kickoffUtc));

  return (
    <div style={{ fontFamily: "system-ui, sans-serif", maxWidth: 820, margin: "32px auto", padding: "0 16px", color: "#1a1a2e" }}>
      <header style={{ display: "flex", alignItems: "baseline", justifyContent: "space-between" }}>
        <h1 style={{ fontSize: 24, margin: 0 }}>⚽ WorldCup Live Hub</h1>
        <span style={{ fontSize: 13, color: conn === "connected" ? "#0a7" : "#c33" }}>
          {conn === "connected" ? "● 即時連線中" : conn === "connecting" ? "○ 連線中…" : "× 已斷線"}
        </span>
      </header>
      <p style={{ color: "#666", fontSize: 13 }}>2026 世界盃 · 比分/狀態即時更新（SignalR）</p>

      <div style={{ display: "grid", gap: 8 }}>
        {rows.map((m) => {
          const live = m.status === "Live";
          return (
            <div
              key={m.externalId}
              style={{
                display: "grid",
                gridTemplateColumns: "1fr auto 1fr",
                alignItems: "center",
                gap: 12,
                padding: "12px 16px",
                borderRadius: 10,
                border: "1px solid #e5e5ef",
                background: live ? "#fff7f7" : "#fafafe",
                transition: "background .4s",
              }}
            >
              <div style={{ textAlign: "right", fontWeight: 600 }}>{teamName(m.homeTeamZh, m.homeTeamEn)}</div>
              <div style={{ textAlign: "center", minWidth: 96 }}>
                <div style={{ fontSize: 22, fontWeight: 700, fontVariantNumeric: "tabular-nums" }}>
                  {m.homeScore} : {m.awayScore}
                </div>
                <div style={{ fontSize: 11, color: live ? "#c33" : "#888" }}>{statusLabel(m)}</div>
              </div>
              <div style={{ textAlign: "left", fontWeight: 600 }}>{teamName(m.awayTeamZh, m.awayTeamEn)}</div>
            </div>
          );
        })}
        {rows.length === 0 && <p style={{ color: "#999" }}>尚無賽事資料…</p>}
      </div>
    </div>
  );
}
