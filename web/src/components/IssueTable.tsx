import { STATUSES, STATUS_LABEL, type Issue, type IssueStatus } from '../api/types'
import { formatPosition, formatTime } from '../lib/format'
import { PriorityBadge, StatusBadge } from './StatusBadge'

interface Props {
  issues: Issue[]
  /** 正在提交的问题 ID，用于禁用该行的选择器并显示“保存中”。 */
  pendingId: string | null
  errorByIssue: Record<string, string>
  onStatusChange: (issue: Issue, status: IssueStatus) => void
  onSelect: (issue: Issue) => void
  selectedId: string | null
  onDelete: (issue: Issue) => void
}

export function IssueTable({
  issues,
  pendingId,
  errorByIssue,
  onStatusChange,
  onSelect,
  selectedId,
  onDelete,
}: Props) {
  return (
    <div className="table-wrap">
      <table className="issue-table">
        <thead>
          <tr>
            <th className="col-id">ID</th>
            <th className="col-title">标题</th>
            <th className="col-priority">优先级</th>
            <th className="col-status">状态</th>
            <th className="col-pos">坐标 (x, y, z)</th>
            <th className="col-time">上报时间</th>
            <th className="col-action">操作</th>
          </tr>
        </thead>
        <tbody>
          {issues.map((issue) => {
            const pending = pendingId === issue.id
            const rowError = errorByIssue[issue.id]
            return (
              <tr
                key={issue.id}
                className={selectedId === issue.id ? 'row selected' : 'row'}
                onClick={() => onSelect(issue)}
              >
                <td className="col-id">
                  <code title={issue.id}>{issue.id}</code>
                </td>
                <td className="col-title">
                  <span className="title-text">{issue.title}</span>
                  {issue.description && <span className="desc-text">{issue.description}</span>}
                </td>
                <td className="col-priority">
                  <PriorityBadge priority={issue.priority} />
                </td>
                <td className="col-status">
                  <StatusBadge status={issue.status} />
                  {rowError && (
                    <div className="row-error" role="alert">
                      {rowError}
                    </div>
                  )}
                </td>
                <td className="col-pos">
                  <code>{formatPosition(issue.position)}</code>
                </td>
                <td className="col-time">{formatTime(issue.createdAt)}</td>
                <td className="col-action">
                  {/* stopPropagation：操作控件不应同时触发行点击（打开详情）。 */}
                  <select
                    aria-label={`修改「${issue.title}」的状态`}
                    value={issue.status}
                    disabled={pending}
                    onClick={(e) => e.stopPropagation()}
                    onChange={(e) => onStatusChange(issue, e.target.value as IssueStatus)}
                  >
                    {STATUSES.map((s) => (
                      <option key={s} value={s}>
                        {STATUS_LABEL[s]}
                      </option>
                    ))}
                  </select>
                  {pending && <span className="pending-dot" aria-label="保存中" />}
                  <button
                    type="button"
                    className="btn-link danger"
                    onClick={(e) => {
                      e.stopPropagation()
                      onDelete(issue)
                    }}
                    disabled={pending}
                  >
                    删除
                  </button>
                </td>
              </tr>
            )
          })}
        </tbody>
      </table>
    </div>
  )
}
