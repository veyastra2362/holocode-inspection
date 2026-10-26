import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { ApiError, deleteIssue, fetchIssues, fetchHealth, updateIssueStatus } from './api/client'
import { PRIORITIES, STATUSES, PRIORITY_LABEL, STATUS_LABEL, type Issue, type IssueStatus, type Priority } from './api/types'
import { IssueDetail } from './components/IssueDetail'
import { IssueTable } from './components/IssueTable'
import { countByStatus, sortIssues } from './lib/format'

type LoadState = 'loading' | 'ready' | 'error'

const POLL_INTERVAL_MS = 10_000

export default function App() {
  const [issues, setIssues] = useState<Issue[]>([])
  const [state, setState] = useState<LoadState>('loading')
  const [loadError, setLoadError] = useState<string>('')
  const [statusFilter, setStatusFilter] = useState<IssueStatus | ''>('')
  const [priorityFilter, setPriorityFilter] = useState<Priority | ''>('')
  const [query, setQuery] = useState('')
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [pendingId, setPendingId] = useState<string | null>(null)
  const [rowErrors, setRowErrors] = useState<Record<string, string>>({})
  const [toast, setToast] = useState<{ kind: 'ok' | 'err'; text: string } | null>(null)
  const [backend, setBackend] = useState<string>('')
  const [lastLoadedAt, setLastLoadedAt] = useState<Date | null>(null)

  // 过滤放到后端（?status=&priority=），搜索框在前端本地过滤。
  const load = useCallback(
    async (opts: { silent?: boolean } = {}) => {
      if (!opts.silent) setState('loading')
      try {
        const [list, health] = await Promise.all([
          fetchIssues({ status: statusFilter, priority: priorityFilter }),
          fetchHealth().catch(() => null),
        ])
        setIssues(list)
        setState('ready')
        setLoadError('')
        setLastLoadedAt(new Date())
        if (health) setBackend(`${health.status} · v${health.version}`)
        else setBackend('unknown')
      } catch (err) {
        const msg = err instanceof ApiError ? err.message : String(err)
        setLoadError(msg)
        setState('error')
      }
    },
    [statusFilter, priorityFilter],
  )

  useEffect(() => {
    void load()
  }, [load])

  // 轮询：Unity 现场上报后，值班人员无需手动刷新即可看到新问题。
  // 页面隐藏时跳过，避免后台标签页无意义地打后端。
  useEffect(() => {
    const timer = window.setInterval(() => {
      if (document.visibilityState === 'visible') void load({ silent: true })
    }, POLL_INTERVAL_MS)
    return () => window.clearInterval(timer)
  }, [load])

  const toastTimer = useRef<number | undefined>(undefined)
  const showToast = useCallback((kind: 'ok' | 'err', text: string) => {
    setToast({ kind, text })
    window.clearTimeout(toastTimer.current)
    toastTimer.current = window.setTimeout(() => setToast(null), 3500)
  }, [])
  useEffect(() => () => window.clearTimeout(toastTimer.current), [])

  const changeStatus = useCallback(
    async (issue: Issue, status: IssueStatus) => {
      if (status === issue.status) return
      const previous = issues
      // 乐观更新：先改 UI，失败再回滚，保证操作手感。
      setIssues((cur) => cur.map((it) => (it.id === issue.id ? { ...it, status } : it)))
      setPendingId(issue.id)
      setRowErrors((cur) => {
        const { [issue.id]: _drop, ...rest } = cur
        return rest
      })
      try {
        const updated = await updateIssueStatus(issue.id, status)
        setIssues((cur) => cur.map((it) => (it.id === updated.id ? updated : it)))
        showToast('ok', `已将「${issue.title}」改为${STATUS_LABEL[status]}`)
      } catch (err) {
        setIssues(previous)
        const msg = err instanceof ApiError ? err.message : String(err)
        setRowErrors((cur) => ({ ...cur, [issue.id]: `状态修改失败：${msg}` }))
        showToast('err', `状态修改失败：${msg}`)
        // 409/404 说明本地数据已过期，重新拉一次。
        if (err instanceof ApiError && (err.status === 404 || err.status === 409)) void load({ silent: true })
      } finally {
        setPendingId(null)
      }
    },
    [issues, load, showToast],
  )

  const removeIssue = useCallback(
    async (issue: Issue) => {
      if (!window.confirm(`确认删除「${issue.title}」？该操作不可撤销。`)) return
      setPendingId(issue.id)
      try {
        await deleteIssue(issue.id)
        setIssues((cur) => cur.filter((it) => it.id !== issue.id))
        if (selectedId === issue.id) setSelectedId(null)
        showToast('ok', '已删除')
      } catch (err) {
        const msg = err instanceof ApiError ? err.message : String(err)
        showToast('err', `删除失败：${msg}`)
      } finally {
        setPendingId(null)
      }
    },
    [selectedId, showToast],
  )

  const visible = useMemo(() => {
    const kw = query.trim().toLowerCase()
    const filtered = kw
      ? issues.filter(
          (it) =>
            it.title.toLowerCase().includes(kw) ||
            it.description.toLowerCase().includes(kw) ||
            it.id.toLowerCase().includes(kw),
        )
      : issues
    return sortIssues(filtered)
  }, [issues, query])

  const counts = useMemo(() => countByStatus(issues), [issues])
  const selected = useMemo(() => issues.find((it) => it.id === selectedId) ?? null, [issues, selectedId])

  return (
    <div className="app">
      <header className="app-header">
        <div>
          <h1>巡检问题管理台</h1>
          <p className="subtitle">
            数据来源：Go 后端 · {backend || '—'}
            {lastLoadedAt && <> · 最近刷新 {lastLoadedAt.toLocaleTimeString('zh-CN', { hour12: false })}</>}
          </p>
        </div>
        <button type="button" className="btn" onClick={() => void load()} disabled={state === 'loading'}>
          {state === 'loading' ? '刷新中…' : '刷新'}
        </button>
      </header>

      <section className="stats" aria-label="状态统计">
        {STATUSES.map((s) => (
          <button
            key={s}
            type="button"
            className={`stat stat-${s} ${statusFilter === s ? 'active' : ''}`}
            onClick={() => setStatusFilter(statusFilter === s ? '' : s)}
            title={statusFilter === s ? '点击取消筛选' : `只看${STATUS_LABEL[s]}`}
          >
            <span className="stat-num">{counts[s]}</span>
            <span className="stat-label">{STATUS_LABEL[s]}</span>
          </button>
        ))}
        <div className="stat stat-total">
          <span className="stat-num">{issues.length}</span>
          <span className="stat-label">当前列表</span>
        </div>
      </section>

      <section className="toolbar">
        <input
          type="search"
          placeholder="搜索标题 / 描述 / ID"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          aria-label="搜索"
        />
        <select value={statusFilter} onChange={(e) => setStatusFilter(e.target.value as IssueStatus | '')} aria-label="状态筛选">
          <option value="">全部状态</option>
          {STATUSES.map((s) => (
            <option key={s} value={s}>
              {STATUS_LABEL[s]}
            </option>
          ))}
        </select>
        <select
          value={priorityFilter}
          onChange={(e) => setPriorityFilter(e.target.value as Priority | '')}
          aria-label="优先级筛选"
        >
          <option value="">全部优先级</option>
          {PRIORITIES.map((p) => (
            <option key={p} value={p}>
              {PRIORITY_LABEL[p]}
            </option>
          ))}
        </select>
        {(statusFilter || priorityFilter || query) && (
          <button
            type="button"
            className="btn-link"
            onClick={() => {
              setStatusFilter('')
              setPriorityFilter('')
              setQuery('')
            }}
          >
            清除筛选
          </button>
        )}
      </section>

      <main className="layout">
        <section className="list">
          {/* 后端不可用：必须给出明确错误页 + 重试，而不是白屏。 */}
          {state === 'error' && (
            <div className="state-error" role="alert">
              <h2>无法加载问题列表</h2>
              <p>{loadError}</p>
              <ul>
                <li>确认 Go 后端已启动（默认 http://localhost:8080）。</li>
                {loadError.includes('无法连接后端') && (
                  <li>开发模式下请检查 vite 代理目标，或直接用 `go run .` 让后端托管本页面。</li>
                )}
                <li>确认数据库文件可写，且端口没有被其他程序占用。</li>
              </ul>
              <button type="button" className="btn" onClick={() => void load()}>
                重试
              </button>
            </div>
          )}

          {state === 'loading' && (
            <div className="state-loading" role="status">
              <span className="spinner" /> 正在加载问题列表…
            </div>
          )}

          {state === 'ready' && visible.length === 0 && (
            <div className="state-empty">
              <h2>{issues.length === 0 ? '还没有任何问题' : '没有符合筛选条件的问题'}</h2>
              <p>
                {issues.length === 0
                  ? '在 Unity 虚拟巡检场景中点击设备或墙面，即可上报第一条问题。'
                  : '试试清除筛选条件。'}
              </p>
            </div>
          )}

          {state === 'ready' && visible.length > 0 && (
            <IssueTable
              issues={visible}
              pendingId={pendingId}
              errorByIssue={rowErrors}
              onStatusChange={(issue, status) => void changeStatus(issue, status)}
              onSelect={(issue) => setSelectedId((cur) => (cur === issue.id ? null : issue.id))}
              selectedId={selectedId}
              onDelete={(issue) => void removeIssue(issue)}
            />
          )}
        </section>

        <IssueDetail
          issue={selected}
          pendingId={pendingId}
          onStatusChange={(issue, status) => void changeStatus(issue, status)}
          onClose={() => setSelectedId(null)}
        />
      </main>

      {toast && (
        <div className={`toast toast-${toast.kind}`} role="status" aria-live="polite">
          {toast.text}
        </div>
      )}
    </div>
  )
}
