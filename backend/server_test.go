package main

import (
	"bytes"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"path/filepath"
	"strings"
	"testing"
)

// newTestServer 起一个使用临时数据库的真实 HTTP 服务。
func newTestServer(t *testing.T) (*httptest.Server, *Store) {
	t.Helper()
	store, err := OpenStore(filepath.Join(t.TempDir(), "test.db"))
	if err != nil {
		t.Fatalf("open store: %v", err)
	}
	t.Cleanup(func() { store.Close() })

	srv := httptest.NewServer(newServer(&api{store: store, version: "test"}, ""))
	t.Cleanup(srv.Close)
	return srv, store
}

type apiResp struct {
	OK     bool            `json:"ok"`
	Code   string          `json:"code"`
	Err    string          `json:"error"`
	Fields FieldErrors     `json:"fields"`
	Data   json.RawMessage `json:"data"`
	Total  int             `json:"total"`
}

func do(t *testing.T, method, url string, body any) (int, apiResp, string) {
	t.Helper()
	var buf *bytes.Reader
	if body != nil {
		b, err := json.Marshal(body)
		if err != nil {
			t.Fatalf("marshal body: %v", err)
		}
		buf = bytes.NewReader(b)
	} else {
		buf = bytes.NewReader(nil)
	}
	req, err := http.NewRequest(method, url, buf)
	if err != nil {
		t.Fatalf("new request: %v", err)
	}
	req.Header.Set("Content-Type", "application/json")
	res, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatalf("%s %s: %v", method, url, err)
	}
	defer res.Body.Close()

	var sb strings.Builder
	if _, err := io.Copy(&sb, res.Body); err != nil {
		t.Fatalf("read body: %v", err)
	}
	var parsed apiResp
	if strings.Contains(res.Header.Get("Content-Type"), "json") {
		if err := json.Unmarshal([]byte(sb.String()), &parsed); err != nil {
			t.Fatalf("unmarshal %q: %v", sb.String(), err)
		}
	}
	return res.StatusCode, parsed, sb.String()
}

func issueFrom(t *testing.T, r apiResp) Issue {
	t.Helper()
	var it Issue
	if err := json.Unmarshal(r.Data, &it); err != nil {
		t.Fatalf("decode issue %s: %v", r.Data, err)
	}
	return it
}

func sampleIssue() map[string]any {
	return map[string]any{
		"title":       "1 号机柜温度异常",
		"description": "机柜右侧温度明显偏高",
		"priority":    "high",
		"position":    map[string]float64{"x": 1.25, "y": 0.8, "z": 2.4},
	}
}

// 测试清单（10 个函数）：
//
//	TestHealth                   健康检查
//	TestCreateAndList            创建 + 列表 + 空列表编码为 []
//	TestValidation               8 个子用例：标题空/空白/超长、非法优先级、缺少或非法坐标、字段拼错
//	TestMalformedJSON            非法 JSON -> 400
//	TestStatusUpdate             PATCH 对象体 / POST 裸字符串 / 非法状态 / 不存在 ID
//	TestDelete                   删除与重复删除
//	TestPersistenceAcrossRestart 关服务重开后数据仍在（题目 3.3 第 7 条）
//	TestCORSPreflight            预检允许 PATCH
//	TestUnknownAPIReturnsJSON404 未知 /api 路径返回 JSON 404
//	TestIDUniqueness             连续创建 50 条 ID 不重复
func TestHealth(t *testing.T) {
	srv, _ := newTestServer(t)
	code, resp, raw := do(t, http.MethodGet, srv.URL+"/api/health", nil)
	if code != 200 || !resp.OK {
		t.Fatalf("health: code=%d body=%s", code, raw)
	}
	if !strings.Contains(raw, `"status":"ok"`) {
		t.Fatalf("health payload missing status: %s", raw)
	}
}

func TestCreateAndList(t *testing.T) {
	srv, _ := newTestServer(t)

	code, resp, raw := do(t, http.MethodPost, srv.URL+"/api/issues", sampleIssue())
	if code != http.StatusCreated {
		t.Fatalf("create: code=%d body=%s", code, raw)
	}
	it := issueFrom(t, resp)
	switch {
	case it.ID == "":
		t.Error("expected server generated id")
	case it.Status != StatusOpen:
		t.Errorf("new issue status = %q, want open", it.Status)
	case it.Title != "1 号机柜温度异常":
		t.Errorf("title = %q", it.Title)
	case it.Priority != PriorityHigh:
		t.Errorf("priority = %q", it.Priority)
	case it.Position.X != 1.25 || it.Position.Y != 0.8 || it.Position.Z != 2.4:
		t.Errorf("position = %+v, want exact hit position", it.Position)
	case it.CreatedAt.IsZero() || it.UpdatedAt.IsZero():
		t.Error("expected createdAt/updatedAt from server")
	}

	code, resp, raw = do(t, http.MethodGet, srv.URL+"/api/issues", nil)
	if code != 200 || resp.Total != 1 {
		t.Fatalf("list: code=%d total=%d body=%s", code, resp.Total, raw)
	}

	// 空列表必须是 []，而不是 null（前端直接 .map 才不会炸）。
	_, resp, raw = do(t, http.MethodGet, srv.URL+"/api/issues?status=resolved", nil)
	if strings.Contains(raw, `"data":null`) {
		t.Fatalf("empty list must encode as [] not null: %s", raw)
	}
	if resp.Total != 0 {
		t.Fatalf("filtered total = %d, want 0", resp.Total)
	}
}

func TestValidation(t *testing.T) {
	srv, _ := newTestServer(t)

	cases := []struct {
		name   string
		body   any
		field  string
		status int
	}{
		{"missing title", map[string]any{"position": map[string]float64{}}, "title", 400},
		{"blank title", map[string]any{"title": "   ", "position": map[string]float64{}}, "title", 400},
		{"title too long", map[string]any{"title": strings.Repeat("长", 121), "position": map[string]float64{}}, "title", 400},
		{"bad priority", map[string]any{"title": "x", "priority": "urgent", "position": map[string]float64{}}, "priority", 400},
		{"missing position", map[string]any{"title": "x"}, "position", 400},
		{"null position", map[string]any{"title": "x", "position": nil}, "position", 400},
		{"huge coordinate", map[string]any{"title": "x", "position": map[string]float64{"x": 1e7}}, "position", 400},
		{"unknown field typo", map[string]any{"title": "x", "postion": map[string]float64{"x": 1}}, "", 400},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			code, resp, raw := do(t, http.MethodPost, srv.URL+"/api/issues", tc.body)
			if code != tc.status {
				t.Fatalf("code = %d, want %d (body=%s)", code, tc.status, raw)
			}
			if resp.OK || resp.Err == "" {
				t.Fatalf("expected error message, got %s", raw)
			}
			if tc.field != "" && resp.Fields[tc.field] == "" {
				t.Fatalf("expected field error for %q, got %s", tc.field, raw)
			}
		})
	}

	// 大小写与首尾空格应被规范化，而不是直接拒绝。
	code, resp, raw := do(t, http.MethodPost, srv.URL+"/api/issues",
		map[string]any{"title": "  机柜异响  ", "priority": "HIGH", "position": map[string]float64{"x": 1}})
	if code != 201 {
		t.Fatalf("normalization: code=%d body=%s", code, raw)
	}
	it := issueFrom(t, resp)
	if it.Title != "机柜异响" || it.Priority != PriorityHigh {
		t.Fatalf("normalized = %q/%q", it.Title, it.Priority)
	}

	// 标题上限 120 个字符（按 rune 计，而不是字节）。
	code, _, raw = do(t, http.MethodPost, srv.URL+"/api/issues",
		map[string]any{"title": strings.Repeat("长", 120), "position": map[string]float64{}})
	if code != 201 {
		t.Fatalf("120 runes should be accepted: code=%d body=%s", code, raw)
	}
}

func TestMalformedJSON(t *testing.T) {
	srv, _ := newTestServer(t)
	req, _ := http.NewRequest(http.MethodPost, srv.URL+"/api/issues", strings.NewReader(`{"title":`))
	req.Header.Set("Content-Type", "application/json")
	res, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	defer res.Body.Close()
	if res.StatusCode != http.StatusBadRequest {
		t.Fatalf("code = %d, want 400", res.StatusCode)
	}
}

func TestStatusUpdate(t *testing.T) {
	srv, _ := newTestServer(t)
	_, resp, _ := do(t, http.MethodPost, srv.URL+"/api/issues", sampleIssue())
	created := issueFrom(t, resp)

	// PATCH + 对象体
	code, resp, raw := do(t, http.MethodPatch, srv.URL+"/api/issues/"+created.ID,
		map[string]any{"status": "in_progress"})
	if code != 200 {
		t.Fatalf("patch: code=%d body=%s", code, raw)
	}
	updated := issueFrom(t, resp)
	if updated.Status != StatusInProgress {
		t.Fatalf("status = %q", updated.Status)
	}
	if !updated.UpdatedAt.After(created.UpdatedAt) {
		t.Fatalf("updatedAt not refreshed: %v -> %v", created.UpdatedAt, updated.UpdatedAt)
	}
	if !updated.CreatedAt.Equal(created.CreatedAt) {
		t.Fatalf("createdAt must not change: %v -> %v", created.CreatedAt, updated.CreatedAt)
	}

	// POST /status + 裸字符串体（兼容 UnityWebRequest 不便发 PATCH 的情况）
	code, resp, raw = do(t, http.MethodPost, srv.URL+"/api/issues/"+created.ID+"/status", "resolved")
	if code != 200 || !strings.Contains(raw, `"status":"resolved"`) {
		t.Fatalf("post status: code=%d body=%s", code, raw)
	}

	// 非法枚举
	code, resp, raw = do(t, http.MethodPatch, srv.URL+"/api/issues/"+created.ID, map[string]any{"status": "done"})
	if code != 400 || resp.Fields["status"] == "" {
		t.Fatalf("invalid status: code=%d body=%s", code, raw)
	}

	// 不存在的 ID
	code, _, raw = do(t, http.MethodPatch, srv.URL+"/api/issues/does-not-exist", map[string]any{"status": "open"})
	if code != 404 || !strings.Contains(raw, "NOT_FOUND") {
		t.Fatalf("missing id: code=%d body=%s", code, raw)
	}
}

func TestDelete(t *testing.T) {
	srv, _ := newTestServer(t)
	_, resp, _ := do(t, http.MethodPost, srv.URL+"/api/issues", sampleIssue())
	created := issueFrom(t, resp)

	if code, _, raw := do(t, http.MethodDelete, srv.URL+"/api/issues/"+created.ID, nil); code != 200 {
		t.Fatalf("delete: code=%d body=%s", code, raw)
	}
	if code, _, _ := do(t, http.MethodGet, srv.URL+"/api/issues/"+created.ID, nil); code != 404 {
		t.Fatalf("get after delete: code=%d, want 404", code)
	}
	if code, _, _ := do(t, http.MethodDelete, srv.URL+"/api/issues/"+created.ID, nil); code != 404 {
		t.Fatalf("double delete: code=%d, want 404", code)
	}
}

// TestPersistenceAcrossRestart 是本项目最关键的一条断言：
// 关闭服务、重新打开同一个数据库文件后，数据必须仍在（题目 3.3 第 7 条）。
func TestPersistenceAcrossRestart(t *testing.T) {
	dir := t.TempDir()
	dbPath := filepath.Join(dir, "persist.db")

	store1, err := OpenStore(dbPath)
	if err != nil {
		t.Fatalf("open #1: %v", err)
	}
	srv1 := httptest.NewServer(newServer(&api{store: store1}, ""))
	_, resp, raw := do(t, http.MethodPost, srv1.URL+"/api/issues", sampleIssue())
	if resp.OK == false {
		t.Fatalf("create failed: %s", raw)
	}
	created := issueFrom(t, resp)
	do(t, http.MethodPatch, srv1.URL+"/api/issues/"+created.ID, map[string]any{"status": "resolved"})
	srv1.Close()
	if err := store1.Close(); err != nil {
		t.Fatalf("close #1: %v", err)
	}

	store2, err := OpenStore(dbPath)
	if err != nil {
		t.Fatalf("reopen: %v", err)
	}
	defer store2.Close()
	srv2 := httptest.NewServer(newServer(&api{store: store2}, ""))
	defer srv2.Close()

	code, resp, raw := do(t, http.MethodGet, srv2.URL+"/api/issues", nil)
	if code != 200 || resp.Total != 1 {
		t.Fatalf("after restart: code=%d total=%d body=%s", code, resp.Total, raw)
	}
	var list []Issue
	if err := json.Unmarshal(resp.Data, &list); err != nil {
		t.Fatal(err)
	}
	if list[0].ID != created.ID || list[0].Status != StatusResolved || list[0].Position.Z != 2.4 {
		t.Fatalf("restored issue mismatch: %+v", list[0])
	}
}

func TestCORSPreflight(t *testing.T) {
	srv, _ := newTestServer(t)
	req, _ := http.NewRequest(http.MethodOptions, srv.URL+"/api/issues", nil)
	req.Header.Set("Origin", "http://localhost:5173")
	req.Header.Set("Access-Control-Request-Method", "POST")
	res, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	defer res.Body.Close()
	if res.StatusCode != http.StatusNoContent {
		t.Fatalf("preflight code = %d", res.StatusCode)
	}
	if got := res.Header.Get("Access-Control-Allow-Origin"); got != "*" {
		t.Fatalf("allow-origin = %q", got)
	}
	if got := res.Header.Get("Access-Control-Allow-Methods"); !strings.Contains(got, "PATCH") {
		t.Fatalf("allow-methods = %q, missing PATCH", got)
	}
}

func TestUnknownAPIReturnsJSON404(t *testing.T) {
	srv, _ := newTestServer(t)
	code, resp, raw := do(t, http.MethodGet, srv.URL+"/api/nope", nil)
	if code != 404 || resp.Code != codeNotFound {
		t.Fatalf("unknown api: code=%d body=%s", code, raw)
	}
}

func TestIDUniqueness(t *testing.T) {
	seen := map[string]struct{}{}
	srv, _ := newTestServer(t)
	for i := 0; i < 50; i++ {
		_, resp, raw := do(t, http.MethodPost, srv.URL+"/api/issues", sampleIssue())
		if !resp.OK {
			t.Fatalf("create #%d failed: %s", i, raw)
		}
		id := issueFrom(t, resp).ID
		if _, dup := seen[id]; dup {
			t.Fatalf("duplicate id %q", id)
		}
		seen[id] = struct{}{}
	}
}
