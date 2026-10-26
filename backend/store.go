package main

import (
	"context"
	"crypto/rand"
	"database/sql"
	"encoding/hex"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"time"

	_ "modernc.org/sqlite" // 纯 Go SQLite 驱动：不需要 CGO / gcc，跨平台直接编译
)

// ErrNotFound 表示按 ID 找不到问题。
var ErrNotFound = errors.New("issue not found")

const timeLayout = time.RFC3339Nano

// Store 是持久化层。题目允许 SQLite 或本地 JSON，这里选择 SQLite：
// 单文件、无需额外服务、事务与唯一约束由数据库保证，比手写 JSON 更不容易写坏数据。
type Store struct {
	db *sql.DB
}

// OpenStore 打开（必要时创建）SQLite 数据库并完成建表。
func OpenStore(path string) (*Store, error) {
	if dir := filepath.Dir(path); dir != "" && dir != "." {
		if err := os.MkdirAll(dir, 0o755); err != nil {
			return nil, fmt.Errorf("create data dir: %w", err)
		}
	}
	dsn := path + "?_pragma=busy_timeout(5000)&_pragma=journal_mode(WAL)&_pragma=foreign_keys(1)"
	db, err := sql.Open("sqlite", dsn)
	if err != nil {
		return nil, fmt.Errorf("open sqlite: %w", err)
	}
	// SQLite 单写入者；串行化连接可以避免 "database is locked" 抖动。
	db.SetMaxOpenConns(1)
	s := &Store{db: db}
	if err := s.migrate(context.Background()); err != nil {
		db.Close()
		return nil, err
	}
	return s, nil
}

func (s *Store) migrate(ctx context.Context) error {
	const schema = `
CREATE TABLE IF NOT EXISTS issues (
	id          TEXT PRIMARY KEY,
	title       TEXT NOT NULL,
	description TEXT NOT NULL DEFAULT '',
	priority    TEXT NOT NULL,
	status      TEXT NOT NULL,
	pos_x       REAL NOT NULL,
	pos_y       REAL NOT NULL,
	pos_z       REAL NOT NULL,
	created_at  TEXT NOT NULL,
	updated_at  TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_issues_created_at ON issues(created_at DESC);
`
	if _, err := s.db.ExecContext(ctx, schema); err != nil {
		return fmt.Errorf("migrate: %w", err)
	}
	return nil
}

func (s *Store) Close() error { return s.db.Close() }

// NewID 生成带时间前缀的唯一 ID（如 iss_1750000000000_3f9a1c2b）。
// 时间前缀让列表按 ID 排序时也大致有序，便于排查问题。
func NewID(now time.Time) string {
	b := make([]byte, 4)
	if _, err := rand.Read(b); err != nil {
		// crypto/rand 在受支持的平台上不会失败；失败时退化为纳秒熵。
		return fmt.Sprintf("iss_%d_%08x", now.UnixMilli(), uint32(now.Nanosecond()))
	}
	return fmt.Sprintf("iss_%d_%s", now.UnixMilli(), hex.EncodeToString(b))
}

// Create 保存新问题：生成 ID、状态置为 open、写入创建/更新时间。
func (s *Store) Create(ctx context.Context, in *Issue) (*Issue, error) {
	now := time.Now().UTC()
	in.ID = NewID(now)
	in.Status = StatusOpen
	in.CreatedAt = now
	in.UpdatedAt = now

	_, err := s.db.ExecContext(ctx, `
INSERT INTO issues (id, title, description, priority, status, pos_x, pos_y, pos_z, created_at, updated_at)
VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
		in.ID, in.Title, in.Description, string(in.Priority), string(in.Status),
		in.Position.X, in.Position.Y, in.Position.Z,
		in.CreatedAt.Format(timeLayout), in.UpdatedAt.Format(timeLayout),
	)
	if err != nil {
		return nil, fmt.Errorf("insert issue: %w", err)
	}
	return in, nil
}

// List 按创建时间倒序返回全部问题；status/priority 为空时不过滤。
func (s *Store) List(ctx context.Context, status, priority string) ([]Issue, error) {
	q := `SELECT id, title, description, priority, status, pos_x, pos_y, pos_z, created_at, updated_at FROM issues`
	var where []string
	var args []any
	if status != "" {
		where = append(where, "status = ?")
		args = append(args, status)
	}
	if priority != "" {
		where = append(where, "priority = ?")
		args = append(args, priority)
	}
	if len(where) > 0 {
		q += " WHERE " + strings.Join(where, " AND ")
	}
	q += " ORDER BY created_at DESC, id DESC"

	rows, err := s.db.QueryContext(ctx, q, args...)
	if err != nil {
		return nil, fmt.Errorf("query issues: %w", err)
	}
	defer rows.Close()

	issues := make([]Issue, 0) // 保证空列表序列化为 []，而不是 null
	for rows.Next() {
		it, err := scanIssue(rows)
		if err != nil {
			return nil, fmt.Errorf("scan issue: %w", err)
		}
		issues = append(issues, it)
	}
	if err := rows.Err(); err != nil {
		return nil, fmt.Errorf("iterate issues: %w", err)
	}
	return issues, nil
}

// Get 按 ID 读取单个问题（走主键查询，不做全表扫描）。
func (s *Store) Get(ctx context.Context, id string) (*Issue, error) {
	row := s.db.QueryRowContext(ctx, `
SELECT id, title, description, priority, status, pos_x, pos_y, pos_z, created_at, updated_at
FROM issues WHERE id = ?`, id)
	it, err := scanIssue(row)
	if errors.Is(err, sql.ErrNoRows) {
		return nil, ErrNotFound
	}
	if err != nil {
		return nil, err
	}
	return &it, nil
}

// rowScanner 同时兼容 *sql.Row 和 *sql.Rows。
type rowScanner interface {
	Scan(dest ...any) error
}

func scanIssue(sc rowScanner) (Issue, error) {
	var (
		it                   Issue
		prio, stat           string
		createdAt, updatedAt string
	)
	if err := sc.Scan(&it.ID, &it.Title, &it.Description, &prio, &stat,
		&it.Position.X, &it.Position.Y, &it.Position.Z, &createdAt, &updatedAt); err != nil {
		return Issue{}, err
	}
	it.Priority, it.Status = Priority(prio), Status(stat)
	created, err := time.Parse(timeLayout, createdAt)
	if err != nil {
		return Issue{}, fmt.Errorf("parse created_at: %w", err)
	}
	updated, err := time.Parse(timeLayout, updatedAt)
	if err != nil {
		return Issue{}, fmt.Errorf("parse updated_at: %w", err)
	}
	it.CreatedAt, it.UpdatedAt = created, updated
	return it, nil
}

// UpdateStatus 修改状态并刷新 updatedAt，返回更新后的问题。
func (s *Store) UpdateStatus(ctx context.Context, id string, status Status) (*Issue, error) {
	now := time.Now().UTC()
	res, err := s.db.ExecContext(ctx,
		`UPDATE issues SET status = ?, updated_at = ? WHERE id = ?`,
		string(status), now.Format(timeLayout), id)
	if err != nil {
		return nil, fmt.Errorf("update status: %w", err)
	}
	n, err := res.RowsAffected()
	if err != nil {
		return nil, fmt.Errorf("rows affected: %w", err)
	}
	if n == 0 {
		return nil, ErrNotFound
	}
	return s.Get(ctx, id)
}

// Delete 删除问题（管理端扩展能力）。
func (s *Store) Delete(ctx context.Context, id string) error {
	res, err := s.db.ExecContext(ctx, `DELETE FROM issues WHERE id = ?`, id)
	if err != nil {
		return fmt.Errorf("delete issue: %w", err)
	}
	n, err := res.RowsAffected()
	if err != nil {
		return fmt.Errorf("rows affected: %w", err)
	}
	if n == 0 {
		return ErrNotFound
	}
	return nil
}
