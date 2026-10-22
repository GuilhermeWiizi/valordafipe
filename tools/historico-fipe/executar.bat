@echo off
setlocal
cd /d "%~dp0"
if not exist ".venv\Scripts\python.exe" (
    echo Execute preparar.bat primeiro.
    exit /b 1
)
if "%~1"=="" (
    ".venv\Scripts\python.exe" -u historico.py plan
) else (
    ".venv\Scripts\python.exe" -u historico.py %*
)
exit /b %errorlevel%
