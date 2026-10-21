<#
.SYNOPSIS
    PostgreSQL portátil para desenvolvimento (alternativa ao docker-compose, sem Docker e sem admin).

.DESCRIPTION
    Na primeira execução baixa os binários oficiais da EnterpriseDB (~330 MB) para .pgsql/,
    inicializa o cluster com as mesmas credenciais do docker-compose (fipe/fipe, banco fipedados)
    e sobe o servidor na porta 5432. Não registra serviço do Windows.

.EXAMPLE
    .\tools\pg-portable.ps1 start    # baixa (1ª vez), inicializa e sobe
    .\tools\pg-portable.ps1 stop
    .\tools\pg-portable.ps1 status
#>
param(
    [ValidateSet('start', 'stop', 'status')]
    [string]$Acao = 'start'
)

$ErrorActionPreference = 'Stop'

$Versao  = '17.9-1'
$Porta   = 5432
$Usuario = 'fipe'
$Senha   = 'fipe'
$Banco   = 'fipedados'

$Raiz  = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\.pgsql'))
$Bin   = Join-Path $Raiz 'pgsql\bin'
$Dados = Join-Path $Raiz 'data'
$Log   = Join-Path $Raiz 'postgres.log'

function Instalar {
    if (Test-Path (Join-Path $Bin 'pg_ctl.exe')) { return }
    New-Item -ItemType Directory -Force $Raiz | Out-Null

    $zip = Join-Path $Raiz "postgresql-$Versao-windows-x64-binaries.zip"
    if (-not (Test-Path $zip)) {
        Write-Host "Baixando PostgreSQL $Versao (~330 MB)..."
        & curl.exe -fsSL --retry 3 -o $zip "https://get.enterprisedb.com/postgresql/postgresql-$Versao-windows-x64-binaries.zip"
        if ($LASTEXITCODE -ne 0) { throw 'Falha no download do PostgreSQL.' }
    }

    # bsdtar do Windows extrai zip; pgAdmin/StackBuilder/doc não são necessários.
    Write-Host 'Extraindo binários...'
    & "$env:SystemRoot\System32\tar.exe" -xf $zip -C $Raiz --exclude 'pgsql/pgAdmin 4' --exclude 'pgsql/StackBuilder' --exclude 'pgsql/doc'
    if ($LASTEXITCODE -ne 0) { throw 'Falha ao extrair o PostgreSQL.' }
    Remove-Item $zip
}

function Inicializar {
    if (Test-Path (Join-Path $Dados 'PG_VERSION')) { return }

    Write-Host 'Inicializando cluster...'
    $pwFile = Join-Path $Raiz 'pw.tmp'
    [IO.File]::WriteAllText($pwFile, $Senha)
    try {
        & (Join-Path $Bin 'initdb.exe') -D $Dados -U $Usuario "--pwfile=$pwFile" -A scram-sha-256 `
            -E UTF8 --locale-provider=icu --icu-locale=pt-BR --locale=C
        if ($LASTEXITCODE -ne 0) { throw 'initdb falhou.' }
    }
    finally {
        Remove-Item $pwFile -ErrorAction SilentlyContinue
    }
}

function EstaRodando {
    & (Join-Path $Bin 'pg_ctl.exe') status -D $Dados *> $null
    return $LASTEXITCODE -eq 0
}

function Iniciar {
    Instalar
    Inicializar

    if (EstaRodando) {
        Write-Host "PostgreSQL já está rodando na porta $Porta."
    }
    else {
        # Sobe via WMI para o servidor nascer fora da árvore/Job Object de quem chamou
        # (terminal, IDE, agente). Iniciado direto, ele morria junto com essa sessão sem
        # desligamento limpo ("database system was not properly shut down").
        $pgCtl = Join-Path $Bin 'pg_ctl.exe'
        $cmd = "`"$pgCtl`" start -D `"$Dados`" -l `"$Log`" -o `"-p $Porta`""
        $r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $cmd; CurrentDirectory = $Raiz }
        if ($r.ReturnValue -ne 0) { throw "Falha ao iniciar o PostgreSQL (WMI retornou $($r.ReturnValue))." }

        $pronto = $false
        for ($i = 0; $i -lt 60 -and -not $pronto; $i++) {
            Start-Sleep -Seconds 1
            & (Join-Path $Bin 'pg_isready.exe') -h localhost -p $Porta *> $null
            $pronto = $LASTEXITCODE -eq 0
        }
        if (-not $pronto) { throw "PostgreSQL não respondeu em 60 s. Veja $Log" }
    }

    $env:PGPASSWORD = $Senha
    $existe = & (Join-Path $Bin 'psql.exe') -h localhost -p $Porta -U $Usuario -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname = '$Banco'"
    if ("$existe".Trim() -ne '1') {
        & (Join-Path $Bin 'createdb.exe') -h localhost -p $Porta -U $Usuario $Banco
        if ($LASTEXITCODE -ne 0) { throw "Falha ao criar o banco $Banco." }
        Write-Host "Banco $Banco criado."
    }
    Write-Host "PostgreSQL pronto: Host=localhost;Port=$Porta;Database=$Banco;Username=$Usuario"
}

switch ($Acao) {
    'start'  { Iniciar }
    'stop'   { & (Join-Path $Bin 'pg_ctl.exe') stop -D $Dados -m fast }
    'status' { & (Join-Path $Bin 'pg_ctl.exe') status -D $Dados }
}
