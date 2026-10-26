import type { Issue } from '../api/types'

export function makeIssue(over: Partial<Issue> = {}): Issue {
  return {
    id: 'iss_1_aaaa',
    title: '1 号机柜温度异常',
    description: '机柜右侧温度明显偏高',
    priority: 'high',
    status: 'open',
    position: { x: 1.25, y: 0.8, z: 2.4 },
    createdAt: '2026-10-26T07:11:03Z',
    updatedAt: '2026-10-26T07:11:03Z',
    ...over,
  }
}
