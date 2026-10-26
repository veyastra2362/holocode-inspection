package main

import (
	"log"
	"net/http"
	"os"
	"strings"
	"time"
)

type statusRecorder struct {
	http.ResponseWriter
	status int
	bytes  int
}

func (s *statusRecorder) WriteHeader(code int) {
	s.status = code
	s.ResponseWriter.WriteHeader(code)
}

func (s *statusRecorder) Write(b []byte) (int, error) {
	if s.status == 0 {
		s.status = http.StatusOK
	}
	n, err := s.ResponseWriter.Write(b)
	s.bytes += n
	return n, err
}

func withLogging(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		start := time.Now()
		rec := &statusRecorder{ResponseWriter: w}
		next.ServeHTTP(rec, r)
		// 静态资源访问量大且噪音大，只记录非静态请求。
		if strings.HasPrefix(r.URL.Path, "/api/") {
			log.Printf("%s %s -> %d (%dB, %s)", r.Method, r.URL.RequestURI(), rec.status, rec.bytes, time.Since(start).Round(time.Microsecond))
		}
	})
}

// withCORS 允许任意来源访问 API。
// 这是内网演示工具：Unity 桌面端不发送 Origin，React 在开发时跑在 5173 端口，
// 部署时可能被放在不同域名下。若要上公网请把 "*" 改成具体白名单。
func withCORS(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		h := w.Header()
		h.Set("Access-Control-Allow-Origin", "*")
		h.Set("Access-Control-Allow-Methods", "GET, POST, PATCH, DELETE, OPTIONS")
		h.Set("Access-Control-Allow-Headers", "Content-Type")
		h.Set("Access-Control-Max-Age", "86400")
		if r.Method == http.MethodOptions {
			w.WriteHeader(http.StatusNoContent)
			return
		}
		next.ServeHTTP(w, r)
	})
}

// newServer 组装完整服务：API + （可选的）前端静态资源。
func newServer(a *api, webDir string) *http.ServeMux {
	mux := http.NewServeMux()
	apiHandler := a.routes()

	// 把所有 /api 请求转给 apiHandler（它自带兜底 404）。
	mux.Handle("/api/", apiHandler)
	mux.Handle("/api", apiHandler)

	if webDir != "" {
		if st, err := os.Stat(webDir); err == nil && st.IsDir() {
			mux.Handle("/", spaHandler(webDir))
		} else {
			log.Printf("web dir %q not found; API-only mode (run `npm run dev` for the UI)", webDir)
		}
	}
	return mux
}
