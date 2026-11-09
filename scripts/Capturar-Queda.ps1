# Capturar-Queda.ps1 - grava o trace USB do Windows ate o iRok ND68 cair uma vez.
# Objetivo: saber QUEM derruba a conexao na porta 1 (o PC resetando a porta por erro,
# ou o proprio teclado se reconectando) e se voce estava parado ou digitando na hora.
# Nao muda nenhuma configuracao: so liga um trace temporario do Windows e desliga no fim.
# Rode como administrador. Para parar antes da queda: crie o arquivo diag\parar.txt

[CmdletBinding()]
param([int]$MaxMinutos = 180)

$ErrorActionPreference = 'Continue'
$dir  = Join-Path $PSScriptRoot 'diag'
New-Item -ItemType Directory -Force $dir | Out-Null
$etl  = Join-Path $dir 'usb-queda.etl'
$log  = Join-Path $dir 'captura.log'
$id   = 'USB\VID_1CA6&PID_052C\20250613'   # iRok ND68 (composite)
$nome = 'irokusb'

function Log([string]$t) { ("{0:HH:mm:ss.fff} {1}" -f (Get-Date), $t) | Tee-Object -FilePath $log -Append }

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "Precisa rodar como administrador." -ForegroundColor Red
    exit 1
}

Add-Type @'
using System; using System.Runtime.InteropServices;
public static class Ocioso {
    [StructLayout(LayoutKind.Sequential)] struct LII { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LII p);
    public static double Segundos() { var l = new LII(); l.cbSize = 8; GetLastInputInfo(ref l); return unchecked((uint)Environment.TickCount - l.dwTime) / 1000.0; }
}
'@

function Chegada { (Get-PnpDeviceProperty -InstanceId $id -KeyName DEVPKEY_Device_LastArrivalDate -ErrorAction SilentlyContinue).Data }
function Presente { [bool](Get-PnpDevice -InstanceId $id -ErrorAction SilentlyContinue).Present }

Remove-Item (Join-Path $dir 'parar.txt') -ErrorAction SilentlyContinue
logman stop $nome -ets 2>$null | Out-Null
Remove-Item $etl -ErrorAction SilentlyContinue

# Hub e controlador xHCI: erros, energia, PnP, maquina de estados, enumeracao, device/hub,
# comandos do controlador. Sem bus trace/IRP (pesado com 8000 Hz). Buffer circular de 512 MB.
logman start $nome -ets -o $etl -f bincirc -max 512 -nb 64 256 -bs 1024 -p Microsoft-Windows-USB-USBHUB3 0x3761B 0xff | Out-Null
logman update $nome -ets -p Microsoft-Windows-USB-USBXHCI 0xB061B 0xff | Out-Null
if (-not (logman query $nome -ets 2>$null | Select-String -Quiet $nome)) { Log "ERRO: o trace nao iniciou."; exit 1 }

$inicio = Chegada
Log "Gravando. Teclado conectado desde $inicio. Use o PC normalmente."
Log "Esta janela fecha a gravacao sozinha logo apos a proxima queda (limite $MaxMinutos min)."

$fim = (Get-Date).AddMinutes($MaxMinutos)
$ocioAntes = 0.0
$motivo = 'tempo limite'
while ((Get-Date) -lt $fim) {
    if (Test-Path (Join-Path $dir 'parar.txt')) { $motivo = 'parado pelo usuario'; break }
    $ocio = [Ocioso]::Segundos()
    if (-not (Presente) -or (Chegada) -ne $inicio) {
        $motivo = 'queda'
        Log ("QUEDA detectada. Tempo sem mexer em teclado/mouse antes da queda: {0:N1} s" -f $ocioAntes)
        break
    }
    $ocioAntes = $ocio
    Start-Sleep -Milliseconds 300
}

if ($motivo -eq 'queda') { Start-Sleep -Seconds 8 }   # deixa gravar a reconexao
logman stop $nome -ets | Out-Null
Log "Gravacao encerrada ($motivo). Reconectado em: $(Chegada)  Presente agora: $(Presente)"
Log "Arquivo: $etl"
Write-Host ""
Write-Host "Pronto. Pode fechar esta janela e avisar o Claude." -ForegroundColor Green
Read-Host "Enter para fechar" | Out-Null
