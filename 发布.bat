@echo off
chcp 65001 >nul
cd /d "%~dp0"

echo ========================================
echo     博客发布
echo ========================================
echo.
echo  勾选要提交的文件，填写提交信息
echo  推送前会自动跑构建与单测
echo.
echo  想看改动清单但不发布，可改用：pnpm deploy status
echo.

call pnpm deploy

if errorlevel 1 (
  echo.
  echo [发布未完成，请看上方提示]
)

echo.
timeout /t 3 /nobreak >nul