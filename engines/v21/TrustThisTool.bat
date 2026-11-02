@echo off
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process powershell -Verb RunAs -Wait -ArgumentList '-NoProfile -ExecutionPolicy Bypass -Command \"& ''%~dp0TrustThisTool.ps1''; Write-Host; Read-Host ''Press Enter to close''\"'"
