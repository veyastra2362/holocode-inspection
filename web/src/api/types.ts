// 与 Go 后端 model.go、Unity IssueData.cs 保持完全一致的字段名。
export type Priority = 'low' | 'medium' | 'high'
export type IssueStatus = 'open' | 'in_progress' | 'resolved'

export interface Vec3 {
  x: number
  y: number
  z: number
}

export interface Issue {
  id: string
  title: string
  description: string
  priority: Priority
  status: IssueStatus
  position: Vec3
  createdAt: string
  updatedAt: string
}

export const PRIORITIES: readonly Priority[] = ['low', 'medium', 'high'] as const
export const STATUSES: readonly IssueStatus[] = ['open', 'in_progress', 'resolved'] as const

export const PRIORITY_LABEL: Record<Priority, string> = {
  low: '低',
  medium: '中',
  high: '高',
}

export const STATUS_LABEL: Record<IssueStatus, string> = {
  open: '待处理',
  in_progress: '处理中',
  resolved: '已解决',
}

/** 运行期校验：后端或代理返回了意料之外的结构时，避免把脏数据渲染进表格。 */
export function isIssue(v: unknown): v is Issue {
  if (typeof v !== 'object' || v === null) return false
  const o = v as Record<string, unknown>
  const pos = o.position as Record<string, unknown> | undefined
  return (
    typeof o.id === 'string' &&
    typeof o.title === 'string' &&
    typeof o.description === 'string' &&
    typeof o.priority === 'string' &&
    (PRIORITIES as readonly string[]).includes(o.priority) &&
    typeof o.status === 'string' &&
    (STATUSES as readonly string[]).includes(o.status) &&
    typeof o.createdAt === 'string' &&
    typeof o.updatedAt === 'string' &&
    typeof pos === 'object' &&
    pos !== null &&
    typeof pos.x === 'number' &&
    typeof pos.y === 'number' &&
    typeof pos.z === 'number'
  )
}
