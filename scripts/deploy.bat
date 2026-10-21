@echo off
setlocal

rem Pode sobrescrever estas variaveis antes de executar o script.
if not defined DEPLOY_HOST set "DEPLOY_HOST=root@srv1684584.hstgr.cloud"
if not defined DEPLOY_PATH set "DEPLOY_PATH=/srv/FipeDados"
if not defined DEPLOY_BRANCH set "DEPLOY_BRANCH=main"

echo Iniciando deploy do FIPE em %DEPLOY_HOST%...
echo Diretorio: %DEPLOY_PATH% - branch: %DEPLOY_BRANCH%
echo.

ssh %DEPLOY_HOST% "cd '%DEPLOY_PATH%' && git pull --ff-only origin '%DEPLOY_BRANCH%' && bash scripts/deploy.sh"
set "DEPLOY_EXIT_CODE=%ERRORLEVEL%"

echo.
if not "%DEPLOY_EXIT_CODE%"=="0" goto falha
echo Deploy do FIPE concluido!
goto fim

:falha
echo FALHA no deploy do FIPE. Codigo: %DEPLOY_EXIT_CODE%
echo Confira a mensagem acima antes de tentar novamente.

:fim
if /I not "%~1"=="--no-pause" pause
exit /b %DEPLOY_EXIT_CODE%
