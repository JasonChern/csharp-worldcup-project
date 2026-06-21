// 比賽時鐘：以伺服器每輪推來的權威分鐘為錨點，前端每秒平滑外推；
// 收到新權威值時校準，但忽略「小幅往回」(來源時間較粗/落後於本地走鐘)以免畫面倒退。

export interface ClockState {
  prefixSec: number;     // 固定前綴秒數（"45:00 +X" 的 45:00；一般盤為 0）
  isStoppage: boolean;   // 是否為傷停 "+X" 格式
  anchorTickSec: number; // 錨點當下「會走的那段」秒數
  anchorMs: number;      // 設定錨點的時間戳 (Date.now)
  running: boolean;
  phase: string | null;
}

function toSec(t: string): number {
  const seg = t.trim().split(":").map((n) => parseInt(n, 10));
  if (seg.some(Number.isNaN)) return NaN;
  return (seg[0] ?? 0) * 60 + (seg[1] ?? 0);
}

export function parseMinute(s: string | null | undefined) {
  if (!s) return null;
  const parts = s.split("+").map((x) => x.trim());
  const main = toSec(parts[0]);
  if (Number.isNaN(main)) return null;
  if (parts.length > 1) {
    const extra = toSec(parts[1]);
    return { prefixSec: main, tickSec: Number.isNaN(extra) ? 0 : extra, isStoppage: true };
  }
  return { prefixSec: 0, tickSec: main, isStoppage: false };
}

export function resync(
  existing: ClockState | undefined,
  minute: string | null | undefined,
  phase: string | null,
  running: boolean,
  nowMs: number
): ClockState | undefined {
  const parsed = parseMinute(minute);
  if (!parsed) return existing ? { ...existing, running, phase } : undefined;

  const fresh: ClockState = {
    prefixSec: parsed.prefixSec,
    isStoppage: parsed.isStoppage,
    anchorTickSec: parsed.tickSec,
    anchorMs: nowMs,
    running,
    phase,
  };
  if (!existing) return fresh;

  const sameSeg = existing.isStoppage === parsed.isStoppage && existing.prefixSec === parsed.prefixSec;
  if (sameSeg && running && existing.running) {
    const curTick = existing.anchorTickSec + Math.floor((nowMs - existing.anchorMs) / 1000);
    const diff = parsed.tickSec - curTick;
    if (diff < 0 && diff > -30) {
      // 來源略落後於本地走鐘，維持本地錨點繼續走，只更新狀態
      return { ...existing, running, phase };
    }
  }
  return fresh;
}

export function display(c: ClockState | undefined, nowMs: number): string | null {
  if (!c) return null;
  const tick = c.anchorTickSec + (c.running ? Math.floor((nowMs - c.anchorMs) / 1000) : 0);
  const fmt = (s: number) => `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`;
  const t = Math.max(0, tick);
  return c.isStoppage ? `${fmt(c.prefixSec)} +${fmt(t)}` : fmt(t);
}

export function isPausedPhase(phase: string | null): boolean {
  const p = (phase ?? "").toLowerCase();
  return p.includes("paus") || p === "ht" || p === "halftime" || p === "break";
}
