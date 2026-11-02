@echo off
rem Generates the desktop shortcut and the in-folder shortcut for THIS copy.
rem Portable: every path is resolved from the folder this file sits in.
cd /d "%~dp0"
if not exist "%~dp0CreateShortcuts.vbs" (
    echo CreateShortcuts.vbs is missing next to this file.
    pause
    exit /b 1
)
wscript.exe "%~dp0CreateShortcuts.vbs"
exit /b %errorlevel%
