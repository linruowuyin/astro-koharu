@echo off
chcp 65001 >nul

echo [正在启动 Astro 文章创建向导...]
echo.

:: 使用 call 确保 pnpm 进程结束后脚本不会直接退出
call pnpm koharu new post

echo.
echo ========================================
echo ✅ 任务结束
echo ========================================

:: 停留 3 秒后自动关闭
timeout /t 3
exit