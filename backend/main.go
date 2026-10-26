// Command server 是巡检系统的 Go 后端。
//
// 同时承担两个角色：
//   - 为 Unity 客户端与 React 管理端提供 REST API；
//   - 直接托管 React 构建产物，使浏览器只访问一个地址（无跨域、无额外部署）。
//
// 用法：
//
//	go run .                      # 监听 :8080，数据库 ./data/inspection.db，静态资源 ./web
//	ADDR=:9000 DB_PATH=/tmp/x.db WEB_DIR= go run .
package main

import (
	"context"
	"errors"
	"flag"
	"log"
	"net/http"
	"os"
	"os/signal"
	"syscall"
	"time"
)

const version = "1.0.0"

func envOr(key, def string) string {
	if v, ok := os.LookupEnv(key); ok {
		return v
	}
	return def
}

func main() {
	var (
		addr   = flag.String("addr", envOr("ADDR", ":8080"), "HTTP 监听地址，例如 :8080")
		dbPath = flag.String("db", envOr("DB_PATH", defaultDBPath()), "SQLite 数据库文件路径")
		webDir = flag.String("web", envOr("WEB_DIR", defaultWebDir()), "React 构建产物目录（为空则只提供 API）")
	)
	flag.Parse()

	log.SetFlags(log.LstdFlags | log.Lmsgprefix)
	log.SetPrefix("[inspection] ")

	store, err := OpenStore(*dbPath)
	if err != nil {
		log.Fatalf("open store: %v", err)
	}
	defer store.Close()

	a := &api{store: store, version: version}
	srv := &http.Server{
		Addr:              *addr,
		Handler:           newServer(a, *webDir),
		ReadHeaderTimeout: 10 * time.Second,
		ReadTimeout:       30 * time.Second,
		WriteTimeout:      30 * time.Second,
		IdleTimeout:       120 * time.Second,
	}

	go func() {
		log.Printf("version %s | listening on http://localhost%s | db=%s | web=%s", version, *addr, *dbPath, orNone(*webDir))
		if err := srv.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
			log.Fatalf("listen: %v", err)
		}
	}()

	// 优雅退出：收到 Ctrl+C 时给在途请求 5 秒收尾，并关闭数据库（WAL 落盘）。
	quit := make(chan os.Signal, 1)
	signal.Notify(quit, os.Interrupt, syscall.SIGTERM)
	<-quit
	log.Println("shutting down…")

	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	if err := srv.Shutdown(ctx); err != nil {
		log.Printf("graceful shutdown failed: %v", err)
	}
	log.Println("stopped")
}

func orNone(s string) string {
	if s == "" {
		return "(disabled)"
	}
	return s
}
