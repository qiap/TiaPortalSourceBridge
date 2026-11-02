@echo off
call "%~dp0BuildProjectIndex.cmd" %*
exit /b %errorlevel%
