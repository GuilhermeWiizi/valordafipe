@echo off
setlocal
cd /d "%~dp0"
if not exist ".venv\Scripts\python.exe" (
    py -3 -m venv .venv
    if errorlevel 1 goto erro
)
".venv\Scripts\python.exe" -m pip install -r requirements.txt
if errorlevel 1 goto erro
if not exist "historico.local.toml" copy /y "config.example.toml" "historico.local.toml" >nul
echo.
echo Pronto. Confira historico.local.toml e execute:
echo   executar.bat plan
echo   executar.bat run
exit /b 0
:erro
echo.
echo Falha na preparacao. Instale Python 3.11 ou superior e confira a mensagem acima.
exit /b 1
