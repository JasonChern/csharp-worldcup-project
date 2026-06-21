// 後端位址：瀏覽器在 host 上執行，故走 host 對外埠（可用 VITE_* 覆寫）
export const MATCH_API =
  import.meta.env.VITE_MATCH_API ?? "http://localhost:5080";
export const REALTIME_URL =
  import.meta.env.VITE_REALTIME_URL ?? "http://localhost:5090";

export const WORLD_CUP_TI = "29614";

export interface Match {
  externalId: string;
  kickoffUtc: string;
  status: string;
  homeScore: number;
  awayScore: number;
  livePhase: string | null;
  matchMinute: string | null;
  tournamentNameZh: string | null;
  homeTeamEn: string;
  homeTeamZh: string | null;
  awayTeamEn: string;
  awayTeamZh: string | null;
}

export async function fetchMatches(): Promise<Match[]> {
  const res = await fetch(`${MATCH_API}/api/matches?tournament=${WORLD_CUP_TI}`);
  if (!res.ok) throw new Error(`fetchMatches failed: ${res.status}`);
  return res.json();
}
