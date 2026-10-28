# Runs installed managed methods on isolated, synthetic objects. No game process,
# HTTP requests, credentials, saves, or installed files are accessed or changed.
# For patched regression checks use tools/test-game-trade-queue.py.
param(
    [string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Screen Stocks'
)
$ErrorActionPreference = 'Stop'
$managed = Join-Path $GameDir 'Screen Stocks_Data\Managed'
$assemblyPath = Join-Path $managed 'Assembly-CSharp.dll'
$reviewedHash = 'ac975fe30dad297c78b5c9adeccfde5a2fa4b2cbbb763000279326fd6d76c826'
if ((Get-FileHash $assemblyPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $reviewedHash) {
    throw 'Game fingerprint changed; review the harness before running it.'
}
[Reflection.Assembly]::LoadFrom((Join-Path $managed 'UnityEngine.CoreModule.dll')) | Out-Null
$assembly = [Reflection.Assembly]::LoadFrom($assemblyPath)
$flags = [Reflection.BindingFlags]'Instance,Public,NonPublic'
$stockType = $assembly.GetType('StockManager', $true)
$actionType = $assembly.GetType('StockManager+ManualTradeAction', $true)
$run = $stockType.GetMethod('RunQueuedManualTradeAsync', $flags)
$zero = [Activator]::CreateInstance($assembly.GetType('BigNumber', $true))

Add-Type @'
using System;
using System.Threading.Tasks;
public static class ManualTradeLockHarness {
    public static async Task<bool> AwaitResult(Task task) { await task; return false; }
    public static void ThrowOnNotification(string stock) {
        throw new InvalidOperationException("Injected pending-state subscriber failure");
    }
}
'@

function New-TestManager {
    $manager = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($stockType)
    $stockType.GetField('_usingRemote', $flags).SetValue($manager, $true)
    $stockType.GetField('_heldManualStocks', $flags).SetValue($manager, [Collections.Generic.HashSet[string]]::new())
    $stockType.GetField('_pending', $flags).SetValue($manager, [Activator]::CreateInstance($assembly.GetType('PendingTradeRegistry')))
    $stockType.GetField('_manualActionTail', $flags).SetValue($manager, [Threading.Tasks.Task]::CompletedTask)
    return $manager
}
function Start-TestTrade($manager, $task, $id) {
    $delegateType = [Func``2].MakeGenericType($actionType, [Threading.Tasks.Task[bool]])
    $parameter = [Linq.Expressions.Expression]::Parameter($actionType, 'action')
    $body = [Linq.Expressions.Expression]::Constant($task, [Threading.Tasks.Task[bool]])
    $execute = [Linq.Expressions.Expression]::Lambda($delegateType, $body, [Linq.Expressions.ParameterExpression[]]@($parameter)).Compile()
    # Do not let PowerShell enumerate/unwrap the returned Task.
    return ,$run.Invoke($manager, [object[]]@($id, $execute, $null, $null, $zero, $zero))
}
function Assert-State($manager, $count, $allowed, $label) {
    $actualCount = $stockType.GetField('_queuedManualActions', $flags).GetValue($manager)
    $actualAllowed = $stockType.GetProperty('CanStartManualTrade').GetValue($manager)
    if ($actualCount -ne $count -or $actualAllowed -ne $allowed) {
        throw "$label failed: queued=$actualCount allowed=$actualAllowed"
    }
    Write-Output "PASS $label : queued=$actualCount allowed=$actualAllowed"
}

# An actual broker completion task feeds the actual StockManager queue.
# Seed the ambiguous state directly; never dispatch a command to the server.
$brokerType = $assembly.GetType('RemoteTradeBroker', $true)
$broker = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($brokerType)
$brokerType.GetField('_sessionLock', $flags).SetValue($broker, [object]::new())
$brokerType.GetField('_sessionCancellation', $flags).SetValue($broker, [Threading.CancellationTokenSource]::new())
$commandType = $assembly.GetType('RemoteTradeBroker+PendingTradeCommand', $true)
$command = [Activator]::CreateInstance($commandType, $true)
$requestId = [Guid]::NewGuid()
$commandType.GetField('RequestId', $flags).SetValue($command, $requestId)
$commandType.GetField('ReplayExpired', $flags).SetValue($command, $true)
$brokerType.GetField('_ambiguousCommand', $flags).SetValue($broker, $command)
$wait = $brokerType.GetMethod('WaitForAmbiguousTradeResultAsync', $flags).Invoke($broker, @($requestId))
if ($wait.IsCompleted) { throw 'Parked command unexpectedly completed.' }
$manager = New-TestManager
$blocked = [ManualTradeLockHarness]::AwaitResult($wait)
$tasks = @()
foreach ($id in @('first', 'second', 'third')) { $tasks += ,(Start-TestTrade $manager $blocked $id) }
Assert-State $manager 3 $false 'Unresolved broker command fills queue'
$brokerType.GetMethod('ResetPendingCommandForSessionBoundary', $flags).Invoke($broker, @())
Assert-State $manager 0 $true 'Real session-boundary method releases waiters'
if (@($tasks | Where-Object { $_.Status -ne 'RanToCompletion' }).Count) { throw 'Queue did not finish cleanly.' }

# A notification exception during ClearPending skips done.TrySetResult.
$manager = New-TestManager
$source = [Threading.Tasks.TaskCompletionSource[bool]]::new()
$first = Start-TestTrade $manager $source.Task 'first'
$second = Start-TestTrade $manager $source.Task 'second'
$registry = $stockType.GetField('_pending', $flags).GetValue($manager)
$subscriber = [Action[string]][Delegate]::CreateDelegate([Action[string]], [ManualTradeLockHarness].GetMethod('ThrowOnNotification'))
$registry.GetType().GetField('OnPendingChanged', $flags).SetValue($registry, $subscriber)
$source.SetResult($false)
$registry.GetType().GetField('OnPendingChanged', $flags).SetValue($registry, $null)
if (-not $first.IsFaulted -or $second.IsCompleted) { throw 'Expected a faulted first task and stranded second task.' }
$third = Start-TestTrade $manager $source.Task 'third'
$fourth = Start-TestTrade $manager $source.Task 'fourth'
Assert-State $manager 3 $false 'Completed trade plus cleanup exception permanently poisons queue'
if ($second.IsCompleted -or $third.IsCompleted -or $fourth.IsCompleted) { throw 'Poisoned queue unexpectedly drained.' }
Write-Output 'All checks passed against the installed game assembly. No live trades were submitted.'
