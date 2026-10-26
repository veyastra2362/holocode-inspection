import { describe, expect, it, vi, beforeEach, afterEach } from 'vitest'
import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import App from './App'
import { makeIssue } from './test/fixtures'

/** 构造一个最小 fetch 桩：按 URL+method 决定返回。 */
function mockFetch(handler: (url: string, init?: RequestInit) => Response | Promise<Response>) {
  const spy = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    return handler(String(input), init)
  })
  vi.stubGlobal('fetch', spy)
  return spy
}

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

beforeEach(() => {
  vi.useRealTimers()
})

afterEach(() => {
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

describe('管理端', () => {
  it('渲染从后端获取的问题：标题、优先级、状态', async () => {
    mockFetch((url) => {
      if (url.includes('/api/health')) return json({ ok: true, data: { status: 'ok', version: '1.0.0', issueCount: 1 } })
      if (url.startsWith('/api/issues')) return json({ ok: true, data: [makeIssue()], total: 1 })
      return json({ ok: false, error: 'not found' }, 404)
    })

    render(<App />)
    expect(await screen.findByText('1 号机柜温度异常')).toBeInTheDocument()
    // 注意：筛选下拉和行内 <option> 里也有“高/待处理”这类文案，
    // 所以用徽章的 class 定位，避免断言到 option 文本。
    expect(document.querySelector('.badge-p-high')).toHaveTextContent('高')
    expect(document.querySelector('.badge-status')).toHaveTextContent('待处理')
    // 坐标原样展示，便于和 Unity 场景对照
    expect(screen.getByText('(1.25, 0.80, 2.40)')).toBeInTheDocument()
  })

  it('修改状态会 PATCH 后端并用返回值更新 UI', async () => {
    const updated = makeIssue({ status: 'in_progress', updatedAt: '2026-10-26T08:00:00Z' })
    const fetchSpy = mockFetch((url, init) => {
      if (url.includes('/api/health')) return json({ ok: true, data: { status: 'ok', version: '1.0.0', issueCount: 1 } })
      if (init?.method === 'PATCH') return json({ ok: true, data: updated })
      if (url.startsWith('/api/issues')) return json({ ok: true, data: [makeIssue()], total: 1 })
      return json({ ok: false }, 404)
    })

    render(<App />)
    const select = await screen.findByRole('combobox', { name: /修改「1 号机柜温度异常」的状态/ })
    await userEvent.selectOptions(select, 'in_progress')

    await waitFor(() => {
      const patch = fetchSpy.mock.calls.find(([, init]) => init?.method === 'PATCH')
      expect(patch).toBeTruthy()
      expect(String(patch![0])).toBe('/api/issues/iss_1_aaaa')
      expect(JSON.parse(String(patch![1]!.body))).toEqual({ status: 'in_progress' })
    })
    await waitFor(() => expect((select as HTMLSelectElement).value).toBe('in_progress'))
    expect(document.querySelector('.badge-status')).toHaveTextContent('处理中')
    expect(screen.getByRole('status')).toHaveTextContent(/改为处理中/)
  })

  it('状态修改失败时回滚并给出错误提示', async () => {
    mockFetch((url, init) => {
      if (url.includes('/api/health')) return json({ ok: true, data: { status: 'ok', version: '1.0.0', issueCount: 1 } })
      if (init?.method === 'PATCH')
        return json({ ok: false, code: 'BAD_REQUEST', error: 'status must be one of "open", "in_progress", "resolved"' }, 400)
      if (url.startsWith('/api/issues')) return json({ ok: true, data: [makeIssue()], total: 1 })
      return json({ ok: false }, 404)
    })

    render(<App />)
    const select = await screen.findByRole('combobox', { name: /修改「1 号机柜温度异常」的状态/ })
    await userEvent.selectOptions(select, 'resolved')

    // 回滚：下拉框重新显示原来的 open
    await waitFor(() => expect((select as HTMLSelectElement).value).toBe('open'))
    expect(screen.getByRole('alert')).toHaveTextContent(/status must be one of/)
  })

  it('后端不可用时显示错误页面而不是白屏', async () => {
    mockFetch(() => {
      throw new TypeError('Failed to fetch')
    })

    render(<App />)
    const alert = await screen.findByRole('alert')
    expect(within(alert).getByText('无法加载问题列表')).toBeInTheDocument()
    expect(within(alert).getByText(/无法连接后端/)).toBeInTheDocument()
    expect(within(alert).getByRole('button', { name: '重试' })).toBeInTheDocument()
  })

  it('后端返回 HTML（例如反代报错页）时给出可读错误', async () => {
    mockFetch(
      (url) =>
        url.includes('/api/health')
          ? json({ ok: true, data: { status: 'ok', version: '1', issueCount: 0 } })
          : new Response('<html><body>502 Bad Gateway</body></html>', { status: 502, headers: { 'Content-Type': 'text/html' } }),
    )

    render(<App />)
    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent(/非 JSON 响应（HTTP 502）/)
  })

  it('列出空数据时显示引导文案', async () => {
    mockFetch((url) => {
      if (url.includes('/api/health')) return json({ ok: true, data: { status: 'ok', version: '1.0.0', issueCount: 0 } })
      return json({ ok: true, data: [], total: 0 })
    })

    render(<App />)
    expect(await screen.findByText('还没有任何问题')).toBeInTheDocument()
  })

  it('筛选条件会带到请求参数里', async () => {
    const fetchSpy = mockFetch((url) => {
      if (url.includes('/api/health')) return json({ ok: true, data: { status: 'ok', version: '1.0.0', issueCount: 0 } })
      return json({ ok: true, data: [], total: 0 })
    })

    render(<App />)
    await screen.findByText('还没有任何问题')
    await userEvent.selectOptions(screen.getByRole('combobox', { name: '优先级筛选' }), 'high')

    await waitFor(() => {
      const urls = fetchSpy.mock.calls.map(([u]) => String(u))
      expect(urls.some((u) => u === '/api/issues?priority=high')).toBe(true)
    })
  })
})
