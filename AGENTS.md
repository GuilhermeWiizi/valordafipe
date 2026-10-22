@C:\Users\Guilherme\.codex\RTK.md

## Deploy na VPS

Quando o usuário pedir deploy, consulte a seção de publicação do `README.md` e use
o fluxo existente em `scripts/deploy.bat` e `scripts/deploy.sh`.

O usuário Windows `Guilherme` já tem acesso SSH por chave configurado e verificado
em 22/10/2026:

- Alias SSH: `fipe`.
- Destino: `root@srv1684584.hstgr.cloud`.
- Configuração local: `C:\Users\Guilherme\.ssh\config`.
- Chave local: `C:\Users\Guilherme\.ssh\fipe_vps`.
- Repositório remoto: `/srv/FipeDados`; branch de publicação: `main`.
- Projeto Docker Compose: `fipedados`; aplicação em `127.0.0.1:8082`.

Use `DEPLOY_HOST=fipe` ao executar `scripts/deploy.bat --no-pause`, para que o SSH
aplique o alias e selecione a chave correta. Preserve o padrão do script para
outras máquinas. O script exige alterações commitadas e o commit enviado à branch
de destino; ele verifica a revisão local contra a VPS antes do build.

Para verificar o acesso sem publicar, execute:

```powershell
rtk proxy ssh -o BatchMode=yes -o StrictHostKeyChecking=yes -o ConnectTimeout=10 fipe "git -C /srv/FipeDados status --short --branch"
```

Mantenha as chaves privadas no perfil do Windows e a configuração `.env.vps` no
servidor. Registre apenas os caminhos e parâmetros necessários, sem copiar ou
exibir o conteúdo desses arquivos. Preserve os outros projetos da VPS e os volumes
do banco. Considere o deploy concluído após o script confirmar a revisão esperada
em `/health/version`, tanto no contêiner quanto no domínio público.
