import { PRIORITY_LABEL, STATUS_LABEL, type IssueStatus, type Priority } from '../api/types'

export function StatusBadge({ status }: { status: IssueStatus }) {
  return <span className={`badge badge-status badge-${status}`}>{STATUS_LABEL[status]}</span>
}

export function PriorityBadge({ priority }: { priority: Priority }) {
  return <span className={`badge badge-priority badge-p-${priority}`}>{PRIORITY_LABEL[priority]}</span>
}
