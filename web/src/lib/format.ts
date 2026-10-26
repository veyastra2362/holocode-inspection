import type { Issue, IssueStatus, Priority } from '../api/types'

const dtf = new Intl.DateTimeFormat('zh-CN', {
  month: '2-digit',
  day: '2-digit',
  hour: '2-digit',
  minute: '2-digit',
  second: '2-digit',
  hour12: false,
})

/** 后端统一返回 UTC ISO8601，这里转成本地时间，避免巡检员看到的时刻差 8 小时。 */
export function formatTime(iso: string): string {
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return iso
  return dtf.format(d).replace(/\//g, '-')
}

export function formatPosition(p: Issue['position']): string {
  return `(${p.x.toFixed(2)}, ${p.y.toFixed(2)}, ${p.z.toFixed(2)})`
}

const PRIORITY_ORDER: Record<Priority, number> = { high: 0, medium: 1, low: 2 }
const STATUS_ORDER: Record<IssueStatus, number> = { open: 0, in_progress: 1, resolved: 2 }

/** 表格的排序：未解决排前面，其次按优先级、再按创建时间倒序。 */
export function sortIssues(issues: Issue[]): Issue[] {
  return [...issues].sort((a, b) => {
    const s = STATUS_ORDER[a.status] - STATUS_ORDER[b.status]
    if (s !== 0) return s
    const p = PRIORITY_ORDER[a.priority] - PRIORITY_ORDER[b.priority]
    if (p !== 0) return p
    return b.createdAt.localeCompare(a.createdAt)
  })
}

export function countByStatus(issues: Issue[]): Record<IssueStatus, number> {
  const acc: Record<IssueStatus, number> = { open: 0, in_progress: 0, resolved: 0 }
  for (const it of issues) acc[it.status]++
  return acc
}
