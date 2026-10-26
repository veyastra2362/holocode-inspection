package main

import (
	"encoding/json"
	"errors"
	"log"
	"net/http"
	"strings"
)

// api 持有 HTTP 处理所需的依赖。
type api struct {
	store *Store
	// version 暴露在 /api/health 中，便于确认线上跑的是哪个构建。
	version string
}

// 统一错误码（写在响应体 code 字段），前端可据此区分提示文案。
const (
	codeBadRequest   = "BAD_REQUEST"
	codeNotFound     = "NOT_FOUND"
	codeInternal     = "INTERNAL"
	codeMethodNotAll = "METHOD_NOT_ALLOWED"
)

type errorBody struct {
	OK     bool        `json:"ok"`
	Code   string      `json:"code"`
	Err    string      `json:"error"`
	Fields FieldErrors `json:"fields,omitempty"`
}

func writeJSON(w http.ResponseWriter, status int, v any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.Header().Set("Cache-Control", "no-store")
	w.WriteHeader(status)
	if err := json.NewEncoder(w).Encode(v); err != nil {
		log.Printf("write json: %v", err)
	}
}

func writeError(w http.ResponseWriter, status int, code, msg string, fields FieldErrors) {
	writeJSON(w, status, errorBody{OK: false, Code: code, Err: msg, Fields: fields})
}

// routes 注册所有路由。注意 GET /api/issues/{id} 只用于调试；
// React 与 Unity 都通过同一个 base URL 访问，因此这里统一放开 CORS。
func (a *api) routes() http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("GET /api/health", a.handleHealth)
	mux.HandleFunc("GET /api/issues", a.handleList)
	mux.HandleFunc("POST /api/issues", a.handleCreate)
	mux.HandleFunc("GET /api/issues/{id}", a.handleGet)
	mux.HandleFunc("PATCH /api/issues/{id}", a.handlePatch)
	mux.HandleFunc("POST /api/issues/{id}/status", a.handlePatch) // 兼容不支持 PATCH 的客户端
	mux.HandleFunc("DELETE /api/issues/{id}", a.handleDelete)
	// 兜底：未知 /api/* 返回 JSON 404，而不是 SPA 的 index.html。
	mux.HandleFunc("/api/", func(w http.ResponseWriter, r *http.Request) {
		writeError(w, http.StatusNotFound, codeNotFound, "no such endpoint: "+r.Method+" "+r.URL.Path, nil)
	})
	return withCORS(withLogging(mux))
}

func (a *api) handleHealth(w http.ResponseWriter, r *http.Request) {
	issues, err := a.store.List(r.Context(), "", "")
	if err != nil {
		writeError(w, http.StatusServiceUnavailable, codeInternal, "database unavailable: "+err.Error(), nil)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{
		"ok": true,
		"data": map[string]any{
			"status":     "ok",
			"version":    a.version,
			"issueCount": len(issues),
		},
	})
}

func (a *api) handleList(w http.ResponseWriter, r *http.Request) {
	q := r.URL.Query()
	status := strings.ToLower(strings.TrimSpace(q.Get("status")))
	priority := strings.ToLower(strings.TrimSpace(q.Get("priority")))
	if status != "" && !ValidStatus(Status(status)) {
		writeError(w, http.StatusBadRequest, codeBadRequest, `status filter must be one of "open", "in_progress", "resolved"`, nil)
		return
	}
	if priority != "" && !ValidPriority(Priority(priority)) {
		writeError(w, http.StatusBadRequest, codeBadRequest, `priority filter must be one of "low", "medium", "high"`, nil)
		return
	}

	issues, err := a.store.List(r.Context(), status, priority)
	if err != nil {
		writeError(w, http.StatusInternalServerError, codeInternal, err.Error(), nil)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"ok": true, "data": issues, "total": len(issues)})
}

func (a *api) handleCreate(w http.ResponseWriter, r *http.Request) {
	// 限制请求体大小，避免被超大 JSON 拖死内存。
	r.Body = http.MaxBytesReader(w, r.Body, 64<<10)

	dec := json.NewDecoder(r.Body)
	dec.DisallowUnknownFields() // 字段拼错（如 "postion"）时明确报错，而不是静默丢弃坐标
	var in CreateIssueInput
	if err := dec.Decode(&in); err != nil {
		writeError(w, http.StatusBadRequest, codeBadRequest, "invalid JSON body: "+err.Error(), nil)
		return
	}

	issue, fields := in.Validate()
	if fields != nil {
		writeError(w, http.StatusBadRequest, codeBadRequest, fields.firstError(), fields)
		return
	}

	created, err := a.store.Create(r.Context(), issue)
	if err != nil {
		log.Printf("create issue: %v", err)
		writeError(w, http.StatusInternalServerError, codeInternal, "failed to save issue", nil)
		return
	}
	writeJSON(w, http.StatusCreated, map[string]any{"ok": true, "data": created})
}

func (a *api) handleGet(w http.ResponseWriter, r *http.Request) {
	issue, err := a.store.Get(r.Context(), r.PathValue("id"))
	if errors.Is(err, ErrNotFound) {
		writeError(w, http.StatusNotFound, codeNotFound, "issue not found", nil)
		return
	}
	if err != nil {
		writeError(w, http.StatusInternalServerError, codeInternal, err.Error(), nil)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"ok": true, "data": issue})
}

// statusPatch 是 PATCH 的请求体，同时接受 {"status":"resolved"} 和裸字符串 "resolved"。
type statusPatch struct {
	Status *string `json:"status"`
}

func (a *api) handlePatch(w http.ResponseWriter, r *http.Request) {
	r.Body = http.MaxBytesReader(w, r.Body, 4<<10)
	var raw json.RawMessage
	if err := json.NewDecoder(r.Body).Decode(&raw); err != nil {
		writeError(w, http.StatusBadRequest, codeBadRequest, "invalid JSON body: "+err.Error(), nil)
		return
	}

	var statusStr string
	var p statusPatch
	if err := json.Unmarshal(raw, &p); err == nil && p.Status != nil {
		statusStr = *p.Status
	} else if err := json.Unmarshal(raw, &statusStr); err != nil {
		writeError(w, http.StatusBadRequest, codeBadRequest,
			`body must be {"status":"open|in_progress|resolved"}`, FieldErrors{"status": "status is required"})
		return
	}

	status := Status(strings.ToLower(strings.TrimSpace(statusStr)))
	if !ValidStatus(status) {
		writeError(w, http.StatusBadRequest, codeBadRequest,
			`status must be one of "open", "in_progress", "resolved"`,
			FieldErrors{"status": `status must be one of "open", "in_progress", "resolved"`})
		return
	}

	updated, err := a.store.UpdateStatus(r.Context(), r.PathValue("id"), status)
	if errors.Is(err, ErrNotFound) {
		writeError(w, http.StatusNotFound, codeNotFound, "issue not found", nil)
		return
	}
	if err != nil {
		writeError(w, http.StatusInternalServerError, codeInternal, err.Error(), nil)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"ok": true, "data": updated})
}

func (a *api) handleDelete(w http.ResponseWriter, r *http.Request) {
	err := a.store.Delete(r.Context(), r.PathValue("id"))
	if errors.Is(err, ErrNotFound) {
		writeError(w, http.StatusNotFound, codeNotFound, "issue not found", nil)
		return
	}
	if err != nil {
		writeError(w, http.StatusInternalServerError, codeInternal, err.Error(), nil)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"ok": true, "data": map[string]string{"id": r.PathValue("id")}})
}
