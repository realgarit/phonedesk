using PhoneDesk.LocalizationGuard;

var repositoryRoot = args.Length == 0 ? Directory.GetCurrentDirectory() : args[0];
var findings = LocalizationGuard.Scan(repositoryRoot);

if (findings.Length == 0)
{
    Console.WriteLine("No findings.");
    return 0;
}

foreach (var finding in findings)
{
    Console.WriteLine(finding);
}

return 1;
