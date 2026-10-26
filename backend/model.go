package main

import "time"

// Priority 是问题优先级枚举。三端（Unity / React / Go）使用同一套字符串值。
type Priority string

// Status 是问题处理状态枚举。新建问题必须为 StatusOpen。
type Status string

const (
	PriorityLow    Priority = "low"
	PriorityMedium Priority = "medium"
	PriorityHigh   Priority = "high"

	StatusOpen       Status = "open"
	StatusInProgress Status = "in_progress"
	StatusResolved   Status = "resolved"
)

// Position 是标记在 Unity 虚拟场景中的世界坐标（Unity 为左手坐标系，y 轴向上）。
type Position struct {
	X float64 `json:"x"`
	Y float64 `json:"y"`
	Z float64 `json:"z"`
}

// Issue 是巡检问题。字段名与评分表第 4 节保持一致：
// id / title / description / priority / status / position / createdAt / updatedAt。
type Issue struct {
	ID          string    `json:"id"`
	Title       string    `json:"title"`
	Description string    `json:"description"`
	Priority    Priority  `json:"priority"`
	Status      Status    `json:"status"`
	Position    Position  `json:"position"`
	CreatedAt   time.Time `json:"createdAt"`
	UpdatedAt   time.Time `json:"updatedAt"`
}

// ValidPriority 报告 p 是否为合法优先级。
func ValidPriority(p Priority) bool {
	switch p {
	case PriorityLow, PriorityMedium, PriorityHigh:
		return true
	}
	return false
}

// ValidStatus 报告 s 是否为合法状态。
func ValidStatus(s Status) bool {
	switch s {
	case StatusOpen, StatusInProgress, StatusResolved:
		return true
	}
	return false
}

// CreateIssueInput 是 POST /api/issues 的请求体。
// position 使用指针以便区分“未提供”和“提供了 (0,0,0)”。
// 指针字段同时保证了：客户端字段拼写错误时不会被静默忽略（见 Validate）。
type CreateIssueInput struct {
	Title       *string   `json:"title"`
	Description *string   `json:"description"`
	Priority    *string   `json:"priority"`
	Position    *Position `json:"position"`
}
