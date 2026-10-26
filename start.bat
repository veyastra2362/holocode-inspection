@echo off
REM One-click start: build web -> build go -> open admin page
REM Windows 批处理对编码敏感，本文件只用 ASCII，避免 cmd 解析中文乱码。
setlocal
cd /d "%~dp0"

where go >nul 2>nul
if errorlevel 1 (
  echo [ERROR] "go" not found. Install Go 1.21+ and add it to PATH.
  exit /b 1
)
where npm >nul 2>nul
if errorlevel 1 (
  echo [ERROR] "npm" not found. Install Node.js 18+.
  exit /b 1
)

echo [1/3] Install and build web front-end...
pushd web
if not exist node_modules (
  call npm install --no-audit --no-fund
  if errorlevel 1 goto :failPushd
)
call npm run build
if errorlevel 1 goto :failPushd
popd

echo [2/3] Build Go backend...
pushd backend
if not exist ..\bin mkdir ..\bin
go build -o ..\bin\inspection-server.exe .
if errorlevel 1 goto :failPushd
popd
if not exist data mkdir data

echo [3/3] Start server on http://localhost:8080
start "" http://localhost:8080
bin\inspection-server.exe -addr=:8080 -db=data\inspection.db -web=web\dist
goto :eof

:failPushd
popd
echo [ERROR] Build failed, see the log above.
exit /b 1
