import { STATUS_LABEL, STATUSES, type Issue, type IssueStatus } from '../api/types'
import { formatPosition, formatTime } from '../lib/format'
import { PriorityBadge, StatusBadge } from './StatusBadge'

interface Props {
  issue: Issue | null
  pendingId: string | null
  onStatusChange: (issue: Issue, status: IssueStatus) => void
  onClose: () => void
}

/** 详情面板：显示完整字段，并给出“复制坐标”便于到 Unity 场景中定位。 */
export function IssueDetail({ issue, pendingId, onStatusChange, onClose }: Props) {
  if (!issue) return null
  const pos = issue.position
  const pending = pendingId === issue.id

  const copyPosition = async () => {
    const text = formatPosition(pos)
    try {
      await navigator.clipboard.writeText(text)
    } catch {
      // 非 https / 无剪贴板权限时静默降级为选中提示
      window.prompt('复制坐标', text)
    }
  }

  return (
    <aside className="detail-panel" aria-label="问题详情">
      <header>
        <h2>问题详情</h2>
        <button type="button" className="btn-link" onClick={onClose} aria-label="关闭详情">
          ✕
        </button>
      </header>

      <h3>{issue.title}</h3>
      <p className="muted">{issue.description || '（无描述）'}</p>

      <dl>
        <dt>ID</dt>
        <dd>
          <code>{issue.id}</code>
        </dd>
        <dt>优先级</dt>
        <dd>
          <PriorityBadge priority={issue.priority} />
        </dd>
        <dt>状态</dt>
        <dd>
          <StatusBadge status={issue.status} />
        </dd>
        <dt>场景坐标</dt>
        <dd>
          <code>{formatPosition(pos)}</code>{' '}
          <button type="button" className="btn-link" onClick={copyPosition}>
            复制
          </button>
        </dd>
        <dt>上报时间</dt>
        <dd>{formatTime(issue.createdAt)}</dd>
        <dt>更新时间</dt>
        <dd>{formatTime(issue.updatedAt)}</dd>
      </dl>

      <div className="detail-status">
        <span>修改状态</span>
        <div className="segmented" role="group" aria-label="修改状态">
          {STATUSES.map((s) => (
            <button
              key={s}
              type="button"
              className={s === issue.status ? 'seg active' : 'seg'}
              disabled={pending || s === issue.status}
              onClick={() => onStatusChange(issue, s)}
            >
              {STATUS_LABEL[s]}
            </button>
          ))}
        </div>
      </div>
    </aside>
  )
}
