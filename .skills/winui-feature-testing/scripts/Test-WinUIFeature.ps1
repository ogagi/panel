[CmdletBinding(DefaultParameterSetName = 'Existing')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Launch')]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string] $ExecutablePath,

    [Parameter(Mandatory, ParameterSetName = 'Existing')]
    [int] $ProcessId,

    [Parameter(Mandatory)]
    [string] $WindowTitle,

    [string] $AutomationId,
    [string] $ExpectedWindowTitle,
    [string[]] $Arguments = @(),
    [ValidateRange(1, 60)] [int] $TimeoutSeconds = 10,
    [ValidateRange(1, 10)] [int] $InvokeCount = 1,
    [switch] $ListControls,
    [switch] $CloseExpectedWindow,
    [switch] $StopLaunchedProcess
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Wait-Until {
    param([scriptblock] $Condition, [string] $FailureMessage)

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $result = & $Condition
        if ($null -ne $result -and $result -ne $false) { return $result }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    throw $FailureMessage
}

function Find-ProcessWindow {
    param([int] $TargetProcessId, [string] $Title)

    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $pidCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        $TargetProcessId)
    $windows = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $pidCondition)
    return $windows | Where-Object { $_.Current.Name -eq $Title } | Select-Object -First 1
}

function Get-ControlRows {
    param([System.Windows.Automation.AutomationElement] $RootElement)

    $condition = [System.Windows.Automation.Condition]::TrueCondition
    foreach ($element in $RootElement.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)) {
        [pscustomobject]@{
            ControlType = $element.Current.ControlType.ProgrammaticName
            Name = $element.Current.Name
            AutomationId = $element.Current.AutomationId
            IsEnabled = $element.Current.IsEnabled
        }
    }
}

$launched = $PSCmdlet.ParameterSetName -eq 'Launch'
$process = if ($launched) {
    Start-Process -FilePath (Resolve-Path -LiteralPath $ExecutablePath) -ArgumentList $Arguments -PassThru
} else {
    Get-Process -Id $ProcessId
}

try {
    $window = Wait-Until { Find-ProcessWindow $process.Id $WindowTitle } "Window '$WindowTitle' was not found for PID $($process.Id)."

    if ($ListControls) {
        Get-ControlRows $window
        return
    }

    if ([string]::IsNullOrWhiteSpace($AutomationId)) {
        throw 'AutomationId is required unless -ListControls is used.'
    }

    $idCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    $control = Wait-Until {
        $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $idCondition)
    } "Control with AutomationId '$AutomationId' was not found."

    for ($index = 1; $index -le $InvokeCount; $index++) {
        $invoke = $control.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $invoke.Invoke()

        $null = Wait-Until { Get-Process -Id $process.Id -ErrorAction SilentlyContinue } "Process $($process.Id) exited after invocation $index."

        if (-not [string]::IsNullOrWhiteSpace($ExpectedWindowTitle)) {
            $expectedWindow = Wait-Until {
                Find-ProcessWindow $process.Id $ExpectedWindowTitle
            } "Expected window '$ExpectedWindowTitle' did not appear after invocation $index."

            if ($CloseExpectedWindow) {
                $windowPattern = $expectedWindow.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
                $windowPattern.Close()
                $null = Wait-Until { -not (Find-ProcessWindow $process.Id $ExpectedWindowTitle) } "Window '$ExpectedWindowTitle' did not close."
            }
        }
    }

    [pscustomobject]@{
        ProcessId = $process.Id
        ProcessAlive = [bool](Get-Process -Id $process.Id -ErrorAction SilentlyContinue)
        AutomationId = $AutomationId
        InvocationCount = $InvokeCount
        ExpectedWindowVisible = if ($ExpectedWindowTitle) { [bool](Find-ProcessWindow $process.Id $ExpectedWindowTitle) } else { $null }
    }
}
finally {
    if ($launched -and $StopLaunchedProcess -and -not $process.HasExited) {
        Stop-Process -Id $process.Id
    }
}
