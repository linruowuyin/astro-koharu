@echo off
chcp 65001 >nul
cd /d "%~dp0"

echo ========================================
echo     回滚线上版本
echo ========================================
echo.
echo  将把线上退回上一个提交
echo  工作区若有未提交改动会拒绝执行
echo.

call pnpm deploy rollback

if errorlevel 1 (
  echo.
  echo [回滚未完成，请看上方提示]
)

echo.
timeout /t 5 /nobreak >nul