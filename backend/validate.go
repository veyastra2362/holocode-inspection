package main

import (
	"math"
	"strings"
	"unicode/utf8"
)

const (
	maxTitleRunes       = 120
	maxDescriptionRunes = 2000
	// 虚拟场景有限，坐标超出该范围的请求一定是误用（例如传了真实世界经纬度）。
	maxAbsCoordinate = 100000
)

// FieldErrors 是字段名 -> 错误信息。响应体 err 字段使用第一个可读的错误作为摘要。
type FieldErrors map[string]string

// Validate 校验创建问题的输入，返回规范化后的 Issue（ID/状态/时间戳由存储层填充）。
func (in *CreateIssueInput) Validate() (*Issue, FieldErrors) {
	errs := FieldErrors{}

	title := ""
	if in.Title == nil {
		errs["title"] = "title is required"
	} else {
		title = strings.TrimSpace(*in.Title)
		switch {
		case title == "":
			errs["title"] = "title is required and must not be blank"
		case utf8.RuneCountInString(title) > maxTitleRunes:
			errs["title"] = "title must be at most 120 characters"
		}
	}

	description := ""
	if in.Description != nil {
		description = strings.TrimSpace(*in.Description)
		if utf8.RuneCountInString(description) > maxDescriptionRunes {
			errs["description"] = "description must be at most 2000 characters"
		}
	}

	priority := PriorityMedium // 默认 medium
	if in.Priority != nil {
		priority = Priority(strings.ToLower(strings.TrimSpace(*in.Priority)))
		if !ValidPriority(priority) {
			errs["priority"] = `priority must be one of "low", "medium", "high"`
		}
	}

	// 位置：必须提供且必须是三个有限数值。
	pos := Position{}
	if in.Position == nil {
		errs["position"] = "position is required, e.g. {\"x\":1,\"y\":0,\"z\":2}"
	} else {
		pos = *in.Position
		switch {
		case !isFinite(pos.X) || !isFinite(pos.Y) || !isFinite(pos.Z):
			errs["position"] = "position x/y/z must be finite numbers"
		case math.Abs(pos.X) > maxAbsCoordinate || math.Abs(pos.Y) > maxAbsCoordinate || math.Abs(pos.Z) > maxAbsCoordinate:
			errs["position"] = "position x/y/z is out of the supported range (±100000)"
		}
	}

	if len(errs) > 0 {
		return nil, errs
	}

	issue := &Issue{
		Title:       title,
		Description: description,
		Priority:    priority,
		Status:      StatusOpen, // 新问题默认 open，且不允许客户端指定状态
		Position:    pos,
	}
	return issue, nil
}

func isFinite(f float64) bool { return !math.IsNaN(f) && !math.IsInf(f, 0) }

// firstError 返回按稳定顺序挑选的第一个字段错误，用于响应中的 err 摘要。
func (fe FieldErrors) firstError() string {
	for _, k := range []string{"title", "description", "priority", "position"} {
		if msg, ok := fe[k]; ok {
			return msg
		}
	}
	for _, msg := range fe {
		return msg
	}
	return "invalid request body"
}
