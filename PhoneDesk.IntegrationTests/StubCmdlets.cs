using System.Management.Automation;

namespace PhoneDesk.IntegrationTests;

public static class ConcurrencyProbe
{
    private static int _active;
    private static int _maxActive;

    public static int MaxActive => Volatile.Read(ref _maxActive);

    public static void Reset()
    {
        Volatile.Write(ref _active, 0);
        Volatile.Write(ref _maxActive, 0);
    }

    public static void Enter()
    {
        var active = Interlocked.Increment(ref _active);
        while (true)
        {
            var observed = Volatile.Read(ref _maxActive);
            if (observed >= active || Interlocked.CompareExchange(ref _maxActive, active, observed) == observed)
            {
                return;
            }
        }
    }

    public static void Exit() => Interlocked.Decrement(ref _active);
}

[Cmdlet(VerbsLifecycle.Invoke, "PhoneDeskSuccess")]
public sealed class InvokePhoneDeskSuccessCommand : PSCmdlet
{
    protected override void ProcessRecord()
    {
        WriteInformation(new InformationRecord("INFO: stub information", MyInvocation.MyCommand.Name));
        WriteWarning("stub warning");
        WriteObject("SUCCESS: stub completed");
        WriteObject("TOPRA: Support RA|support@contoso.example|ra-1|+41440000000|CallQueue|True");
        WriteObject("TOPCQ: Support Queue|cq-1|Attendant|30|agent-1|group-1|ra-1");
        WriteObject("TOPGRP: Support|group-1|support|Support team");
    }
}

[Cmdlet(VerbsLifecycle.Invoke, "PhoneDeskError")]
public sealed class InvokePhoneDeskErrorCommand : PSCmdlet
{
    protected override void ProcessRecord()
    {
        WriteError(new ErrorRecord(
            new InvalidOperationException("stub error"),
            "PhoneDeskStubError",
            ErrorCategory.InvalidOperation,
            targetObject: null));
    }
}

[Cmdlet(VerbsLifecycle.Invoke, "PhoneDeskThrow")]
public sealed class InvokePhoneDeskThrowCommand : PSCmdlet
{
    protected override void ProcessRecord() => throw new InvalidOperationException("stub exception");
}

[Cmdlet(VerbsLifecycle.Invoke, "PhoneDeskMalformed")]
public sealed class InvokePhoneDeskMalformedCommand : PSCmdlet
{
    protected override void ProcessRecord()
    {
        WriteObject("TOPRA: missing|fields");
        WriteObject("TOPCQ: incomplete");
        WriteObject("TOPGRP: Valid Group|group-valid|valid|Valid row survives");
    }
}

[Cmdlet(VerbsLifecycle.Invoke, "PhoneDeskEmpty")]
public sealed class InvokePhoneDeskEmptyCommand : PSCmdlet
{
    protected override void ProcessRecord()
    {
    }
}

[Cmdlet(VerbsLifecycle.Invoke, "PhoneDeskProbe")]
public sealed class InvokePhoneDeskProbeCommand : PSCmdlet
{
    [Parameter(Mandatory = true)]
    public string Name { get; set; } = string.Empty;

    [Parameter]
    [ValidateRange(1, 2_000)]
    public int DelayMilliseconds { get; set; } = 200;

    protected override void ProcessRecord()
    {
        ConcurrencyProbe.Enter();
        try
        {
            Thread.Sleep(DelayMilliseconds);
            WriteObject($"SUCCESS: {Name}");
        }
        finally
        {
            ConcurrencyProbe.Exit();
        }
    }
}
