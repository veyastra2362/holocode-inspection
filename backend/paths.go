package main

import (
	"os"
	"path/filepath"
)

// defaultDBPath 让 `go run .` 在 backend/ 目录下直接可用（数据落在 backend/data/）。
func defaultDBPath() string {
	return filepath.Join("data", "inspection.db")
}

// defaultWebDir 指向前端构建产物 web/dist。
// 同时兼容两种运行目录：backend/ 下执行 go run .，或在仓库根目录执行 build 出来的二进制。
func defaultWebDir() string {
	candidates := []string{
		filepath.Join("..", "web", "dist"), // cwd = backend/
		filepath.Join("web", "dist"),       // cwd = repo root
		"dist",                             // cwd = web/
	}
	for _, c := range candidates {
		if st, err := os.Stat(filepath.Join(c, "index.html")); err == nil && !st.IsDir() {
			return c
		}
	}
	return filepath.Join("..", "web", "dist")
}
