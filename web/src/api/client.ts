import { isIssue, type Issue, type IssueStatus, type Priority } from './types'

// 默认空字符串 = 同源请求（开发时走 vite 代理，生产时由 Go 托管页面并同时提供 /api）。
// 可用 VITE_API_BASE 指向其他后端，例如 Unity 部署在内网服务器上时。
const BASE = (import.meta.env.VITE_API_BASE ?? '').replace(/\/+$/, '')

/** ApiError 携带 HTTP 状态码与后端返回的字段级错误，便于页面给出精准提示。 */
export class ApiError extends Error {
  readonly status: number
  readonly code: string
  readonly fields?: Record<string, string>

  constructor(message: string, status: number, code = 'UNKNOWN', fields?: Record<string, string>) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.code = code
    this.fields = fields
  }

  /** 网络层错误（后端没启动 / 地址写错 / 超时）时 status 为 0。 */
  get isNetworkError(): boolean {
    return this.status === 0
  }
}

interface Envelope<T> {
  ok: boolean
  data?: T
  error?: string
  code?: string
  fields?: Record<string, string>
  total?: number
}

const TIMEOUT_MS = 8000

async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
  const controller = new AbortController()
  const timer = setTimeout(() => controller.abort(), TIMEOUT_MS)

  let res: Response
  try {
    res = await fetch(`${BASE}${path}`, {
      ...init,
      signal: controller.signal,
      headers: {
        Accept: 'application/json',
        ...(init.body ? { 'Content-Type': 'application/json' } : {}),
        ...init.headers,
      },
    })
  } catch (err) {
    clearTimeout(timer)
    if (err instanceof DOMException && err.name === 'AbortError') {
      throw new ApiError(`请求超时（${TIMEOUT_MS / 1000} 秒）：${BASE || '同源'}${path}`, 0, 'TIMEOUT')
    }
    throw new ApiError(`无法连接后端（${BASE || '同源'}）：请确认 Go 服务已启动`, 0, 'NETWORK')
  } finally {
    clearTimeout(timer)
  }

  // 后端挂掉时反代/网关可能返回 HTML，这里必须按文本读取再尝试解析，
  // 否则 res.json() 抛出的 SyntaxError 会变成难以理解的报错（甚至白屏）。
  const text = await res.text()
  let body: Envelope<T> | null = null
  if (text) {
    try {
      body = JSON.parse(text) as Envelope<T>
    } catch {
      body = null
    }
  }

  if (!res.ok) {
    const message =
      body?.error ??
      (body === null && text.trimStart().startsWith('<')
        ? `后端返回了非 JSON 响应（HTTP ${res.status}），可能是代理或服务未就绪`
        : `请求失败（HTTP ${res.status}）`)
    throw new ApiError(message, res.status, body?.code ?? 'HTTP_ERROR', body?.fields)
  }

  if (body === null) {
    throw new ApiError('后端响应不是合法 JSON', res.status, 'BAD_RESPONSE')
  }
  if (!body.ok) {
    throw new ApiError(body.error ?? '后端返回失败', res.status, body.code ?? 'UNKNOWN', body.fields)
  }
  return body.data as T
}

export async function fetchIssues(params: { status?: IssueStatus | ''; priority?: Priority | '' } = {}): Promise<Issue[]> {
  const qs = new URLSearchParams()
  if (params.status) qs.set('status', params.status)
  if (params.priority) qs.set('priority', params.priority)
  const suffix = qs.toString() ? `?${qs.toString()}` : ''

  const data = await request<unknown>(`/api/issues${suffix}`)
  if (!Array.isArray(data)) {
    throw new ApiError('后端返回的问题列表格式不正确', 200, 'BAD_RESPONSE')
  }
  // 单条脏数据不应导致整页崩溃：过滤掉并留痕。
  const issues = data.filter((item) => {
    const ok = isIssue(item)
    if (!ok) console.warn('[api] 忽略结构不符合预期的问题数据', item)
    return ok
  })
  return issues
}

export async function updateIssueStatus(id: string, status: IssueStatus): Promise<Issue> {
  return request<Issue>(`/api/issues/${encodeURIComponent(id)}`, {
    method: 'PATCH',
    body: JSON.stringify({ status }),
  })
}

export async function deleteIssue(id: string): Promise<void> {
  await request<{ id: string }>(`/api/issues/${encodeURIComponent(id)}`, { method: 'DELETE' })
}

export async function fetchHealth(): Promise<{ status: string; version: string; issueCount: number }> {
  return request<{ status: string; version: string; issueCount: number }>('/api/health')
}
