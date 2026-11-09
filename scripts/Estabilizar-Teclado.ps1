# Estabilizar-Teclado.ps1 - impede o Windows de suspender o iRok ND68.
# Alvo: quedas/piscadas do teclado depois de um tempo parado na porta USB.
# Rode como administrador.  Para reverter:  .\Estabilizar-Teclado.ps1 -Desfazer

[CmdletBinding()]
param([switch]$Desfazer)

$ErrorActionPreference = 'Stop'
$VID_PID = 'VID_1CA6&PID_052C'   # iRok ND68

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "Precisa rodar como administrador." -ForegroundColor Red
    Write-Host "Clique com o botao direito no arquivo e escolha 'Executar com o PowerShell' como admin,"
    Write-Host "ou abra o Terminal como administrador e rode o script de novo."
    exit 1
}

$valor = if ($Desfazer) { 1 } else { 0 }
$acao  = if ($Desfazer) { 'REVERTENDO (voltando ao padrao do Windows)' } else { 'DESLIGANDO a economia de energia do teclado' }
Write-Host "== $acao ==" -ForegroundColor Cyan

# 1) Plano de energia: suspensao seletiva do USB (CA e bateria)
#    0 = desabilitada. Mexe no plano ativo apenas.
$sub = '2a737441-1930-4402-8d77-b2bebba308a3'
$cfg = '48e6b7a6-50f5-4782-a5d4-53bb8f07e226'
powercfg /setacvalueindex SCHEME_CURRENT $sub $cfg $valor | Out-Null
powercfg /setdcvalueindex SCHEME_CURRENT $sub $cfg $valor | Out-Null
powercfg /setactive SCHEME_CURRENT | Out-Null
Write-Host ("  [ok] Plano de energia: suspensao seletiva USB = " + $(if ($valor -eq 0) { 'desabilitada' } else { 'habilitada (padrao)' }))

# 2) Chaves por dispositivo: o teclado (pai + interfaces) e o hub raiz onde ele esta.
#    EnhancedPowerManagementEnabled e o que realmente deixa o teclado "dormir".
$alvos = @()
$alvos += (Get-PnpDevice -PresentOnly | Where-Object { $_.InstanceId -like "*$VID_PID*" -and $_.InstanceId -like 'USB\*' }).InstanceId
$alvos += (Get-PnpDevice -PresentOnly | Where-Object { $_.InstanceId -like 'USB\ROOT_HUB*' }).InstanceId
$alvos = $alvos | Where-Object { $_ } | Select-Object -Unique

if (-not $alvos) { Write-Host "  [aviso] Teclado nao encontrado. Ligue o teclado e rode de novo." -ForegroundColor Yellow }

foreach ($id in $alvos) {
    $path = "HKLM:\SYSTEM\CurrentControlSet\Enum\$id\Device Parameters"
    if (-not (Test-Path $path)) { continue }
    foreach ($nome in 'EnhancedPowerManagementEnabled', 'AllowIdleIrpInD3', 'SelectiveSuspendEnabled', 'DeviceSelectiveSuspendEnabled') {
        # Só escreve se a chave existir ou se estivermos desligando (criar com 0 é o que trava a suspensão).
        $existe = $null -ne (Get-ItemProperty -Path $path -Name $nome -ErrorAction SilentlyContinue)
        if ($existe -or -not $Desfazer) {
            New-ItemProperty -Path $path -Name $nome -Value $valor -PropertyType DWord -Force | Out-Null
        }
    }
    $curto = if ($id.Length -gt 58) { $id.Substring(0, 58) + '...' } else { $id }
    Write-Host "  [ok] $curto"
}

Write-Host ""
Write-Host "Pronto. Agora:" -ForegroundColor Green
Write-Host "  1. Desconecte e reconecte o teclado na porta 1 (a do lado do RJ45)."
Write-Host "  2. Abra o MonitorTeclado.exe e deixe aberto enquanto usa o PC."
Write-Host "  3. Confira se ele mostra 480 Mbps / 8000 Hz e se 'Quedas' fica em 0."
Write-Host ""
Write-Host "Falta um passo manual que o Windows so aceita pela interface:" -ForegroundColor Yellow
Write-Host "  Gerenciador de Dispositivos > o teclado e os 'USB Root Hub' >"
Write-Host "  aba Gerenciamento de Energia > DESMARQUE 'Permitir que o computador"
Write-Host "  desligue este dispositivo para economizar energia'."
