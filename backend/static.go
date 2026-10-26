package main

import (
	"net/http"
	"os"
	"path"
	"path/filepath"
	"strings"
)

// spaHandler 提供 React 构建产物：
//   - 存在的静态文件直接返回（带长缓存，文件名里通常有 hash）；
//   - 其余路径返回 index.html，交给前端路由处理；
//   - 绝不把 index.html 当成 JS/CSS 返回，否则浏览器会报 MIME 错误且白屏。
func spaHandler(root string) http.Handler {
	fs := http.FileServer(http.Dir(root))
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		clean := path.Clean("/" + r.URL.Path)
		file := filepath.Join(root, filepath.FromSlash(clean))

		if st, err := os.Stat(file); err == nil && !st.IsDir() {
			if clean != "/index.html" {
				w.Header().Set("Cache-Control", "public, max-age=31536000, immutable")
			} else {
				w.Header().Set("Cache-Control", "no-cache")
			}
			fs.ServeHTTP(w, r)
			return
		}

		// 带扩展名的路径（.js/.css/.map…）如果不存在，就是真的 404，不能回 index.html。
		if ext := path.Ext(clean); ext != "" && ext != ".html" {
			http.NotFound(w, r)
			return
		}
		if strings.HasPrefix(clean, "/api/") {
			w.Header().Set("Content-Type", "application/json; charset=utf-8")
			w.WriteHeader(http.StatusNotFound)
			_, _ = w.Write([]byte(`{"ok":false,"code":"NOT_FOUND","error":"no such endpoint"}` + "\n"))
			return
		}

		w.Header().Set("Cache-Control", "no-cache")
		http.ServeFile(w, r, filepath.Join(root, "index.html"))
	})
}
