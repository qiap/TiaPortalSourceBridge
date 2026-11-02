@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "try { Invoke-RestMethod -Method Post -Uri http://localhost:18980/api/shutdown -ErrorAction Stop | Out-Null; Write-Host '已请求安全退出，正在执行的任务完成后自动结束。' } catch { Write-Host '看板服务未运行。' }"
