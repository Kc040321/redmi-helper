#Requires -RunAsAdministrator
# Redmi Helper - 电源计划一键优化
# 用法：管理员 PowerShell 里运行
#   powershell -ExecutionPolicy Bypass -File .\tune-power-plans.ps1
# 改之前会把所有计划导出到桌面备份，恢复方法见最后的输出。
#
# 四个计划的分工（值格式：插电 / 电池）：
#   节能     日常省电、静谧模式   关睿频，PCIe/USB/WiFi 全省电，息屏和睡眠最快
#   平衡     日常使用             睿频开启，插电偏性能、电池偏省电
#   高性能   打游戏               睿频积极，但保留空闲降频（最低处理器状态 5%），不会一直烫
#   卓越性能 跑分、长时间满载     全部拉满，空闲也不降频，不适合日常

$ErrorActionPreference = 'Continue'

# ---------- 设置项 GUID ----------
$SUB_PROCESSOR = '54533251-82be-4824-96c1-47b60b740d00'
$PROC_MIN      = '893dee8e-2bef-41e0-89c6-b55d0929964c'   # 最小处理器状态 %
$PROC_MAX      = 'bc5038f7-23e0-4960-96da-33abaf5935ec'   # 最大处理器状态 %
$BOOST_MODE    = 'be337238-0d82-4146-a960-4f3749d470c7'   # 处理器性能提升模式：0 关 1 开 2 积极
$EPP           = '36687f9e-e3a5-4dbf-b1dc-15eb381c6863'   # 能效/性能偏好 0~100，越小越偏性能
$SUB_PCIE      = '501a4d13-42af-4429-9fd1-a8218c268e20'
$PCIE_ASPM     = 'ee12f906-d277-404b-b6da-e5fa1a576df5'   # 链接状态电源管理：0 关 1 中等 2 最大省电
$SUB_USB       = '2a737441-1930-4402-8d77-b2bebba308a3'
$USB_SUSPEND   = '48e6b7a6-50f5-4782-a5d4-53bb8f07e226'   # USB 选择性暂停：0 关 1 开
$SUB_WIFI      = '19cbb8fa-5279-450e-9fac-8a3d5fedd0c1'
$WIFI_SAVE     = '12bbebe6-58d6-4636-95bb-3217ef867c1a'   # 无线节能：0 最高性能 1 低 2 中 3 最大
$SUB_VIDEO     = '7516b95f-f776-4464-8c53-06167f40cc99'
$VIDEO_OFF     = '3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e'   # 关闭显示器（秒，0 = 从不）
$SUB_SLEEP     = '238c9fa8-0aad-41ed-83f4-97be242c8f20'
$SLEEP_AFTER   = '29f6c1db-86da-48c5-9fdb-f2b67b1f44da'   # 睡眠（秒，0 = 从不）

# ---------- 每个计划的取值：@(插电, 电池) ----------
$profiles = @{
    saver = @{
        $PROC_MIN = @(5, 5);    $PROC_MAX = @(100, 100); $BOOST_MODE = @(0, 0); $EPP = @(70, 85)
        $PCIE_ASPM = @(2, 2);   $USB_SUSPEND = @(1, 1);  $WIFI_SAVE = @(1, 3)
        $VIDEO_OFF = @(300, 180); $SLEEP_AFTER = @(1200, 600)
    }
    balanced = @{
        $PROC_MIN = @(5, 5);    $PROC_MAX = @(100, 100); $BOOST_MODE = @(2, 1); $EPP = @(33, 60)
        $PCIE_ASPM = @(1, 2);   $USB_SUSPEND = @(1, 1);  $WIFI_SAVE = @(0, 2)
        $VIDEO_OFF = @(600, 300); $SLEEP_AFTER = @(1800, 900)
    }
    high = @{
        $PROC_MIN = @(5, 5);    $PROC_MAX = @(100, 100); $BOOST_MODE = @(2, 2); $EPP = @(10, 33)
        $PCIE_ASPM = @(0, 1);   $USB_SUSPEND = @(0, 1);  $WIFI_SAVE = @(0, 0)
        $VIDEO_OFF = @(900, 300); $SLEEP_AFTER = @(0, 900)
    }
    ultimate = @{
        $PROC_MIN = @(100, 100); $PROC_MAX = @(100, 100); $BOOST_MODE = @(2, 2); $EPP = @(0, 0)
        $PCIE_ASPM = @(0, 0);    $USB_SUSPEND = @(0, 0);  $WIFI_SAVE = @(0, 0)
        $VIDEO_OFF = @(900, 300); $SLEEP_AFTER = @(0, 900)
    }
}
$subOf = @{
    $PROC_MIN = $SUB_PROCESSOR; $PROC_MAX = $SUB_PROCESSOR; $BOOST_MODE = $SUB_PROCESSOR; $EPP = $SUB_PROCESSOR
    $PCIE_ASPM = $SUB_PCIE; $USB_SUSPEND = $SUB_USB; $WIFI_SAVE = $SUB_WIFI
    $VIDEO_OFF = $SUB_VIDEO; $SLEEP_AFTER = $SUB_SLEEP
}

function Get-Plans {
    $list = @()
    foreach ($line in (powercfg /list)) {
        if ($line -match '([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s+\((.+?)\)') {
            $list += [pscustomobject]@{ Guid = $matches[1]; Name = $matches[2] }
        }
    }
    return $list
}

function Get-Kind($name) {
    if ($name -match '卓越|Ultimate') { return 'ultimate' }
    if ($name -match '高性能|High performance') { return 'high' }
    if ($name -match '节能|省电|Power saver') { return 'saver' }
    if ($name -match '平衡|Balanced') { return 'balanced' }
    return $null
}

# ---------- 1. 没有节能计划就从 Windows 模板建一个 ----------
$plans = Get-Plans
if (-not ($plans | Where-Object { (Get-Kind $_.Name) -eq 'saver' })) {
    Write-Host '没有「节能」计划，从 Windows 模板创建一个……'
    powercfg -duplicatescheme a1841308-3541-4fab-bc81-f71556f20b4a | Out-Null
    $plans = Get-Plans
}

# ---------- 2. 备份 ----------
$backup = Join-Path ([Environment]::GetFolderPath('Desktop')) ('电源计划备份-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $backup | Out-Null
foreach ($p in $plans) {
    $safe = ($p.Name -replace '[\\/:*?"<>|]', '_')
    powercfg /export (Join-Path $backup "$safe.pow") $p.Guid | Out-Null
}
Write-Host "已备份到：$backup" -ForegroundColor Green

# ---------- 3. 逐个计划写入 ----------
foreach ($p in $plans) {
    $kind = Get-Kind $p.Name
    if (-not $kind) { Write-Host "跳过「$($p.Name)」（认不出是哪种计划）"; continue }
    $values = $profiles[$kind]
    foreach ($setting in $values.Keys) {
        $sub = $subOf[$setting]
        powercfg /setacvalueindex $p.Guid $sub $setting $values[$setting][0] | Out-Null
        powercfg /setdcvalueindex $p.Guid $sub $setting $values[$setting][1] | Out-Null
    }
    Write-Host "已优化「$($p.Name)」→ $kind" -ForegroundColor Cyan
}

# ---------- 4. 重新激活当前计划，让修改立即生效 ----------
if ((powercfg /getactivescheme) -match '([0-9a-fA-F-]{36})') { powercfg /setactive $matches[1] }

Write-Host ''
Write-Host '完成。想用回某个计划原来的设置：在管理员 PowerShell 里运行' -ForegroundColor Yellow
Write-Host '  powercfg /import "备份文件夹\计划名.pow"' -ForegroundColor Yellow
Write-Host '它会作为一个新计划导入，切换过去即可；不需要的计划可以在控制面板 → 电源选项里删除。' -ForegroundColor Yellow
