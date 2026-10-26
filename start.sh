#!/usr/bin/env bash
# 一键启动（macOS / Linux / WSL）：
#   ./start.sh
# 说明：Windows 用户可直接双击 start.bat
set -euo pipefail
cd "$(dirname "$0")"

command -v go >/dev/null 2>&1 || { echo "[ERROR] 未找到 go，请先安装 Go 1.21+"; exit 1; }

echo "[1/3] 安装并构建前端（web/dist）..."
pushd web >/dev/null
[ -d node_modules ] || npm install --no-audit --no-fund
npm run build
popd >/dev/null

echo "[2/3] 编译 Go 后端..."
pushd backend >/dev/null
go build -o ../bin/inspection-server .
popd >/dev/null

echo "[3/3] 启动服务：http://localhost:8080"
exec ./bin/inspection-server -addr=:8080 -db=data/inspection.db -web=web/dist
